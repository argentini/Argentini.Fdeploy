// ReSharper disable ConvertIfStatementToSwitchStatement

using Spectre.Console;
using YamlDotNet.Serialization;
// ReSharper disable RedundantBoolCompare
// ReSharper disable ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
// ReSharper disable AccessToModifiedClosure

namespace Argentini.Fdeploy.Domain;

public sealed class AppRunner
{
    #region Constants

    private static string CliErrorPrefix => "  • ";

    #endregion

    #region Run Mode Properties

    public bool VersionMode { get; set; }
    public bool InitMode { get; set; }
    public bool HelpMode { get; set; }

    #endregion
    
    #region App State Properties

    private List<string> CliArguments { get; } = [];
    public AppState AppState { get; } = new();

    #endregion
    
    #region Properties

    private Stopwatch Timer { get; } = new();

    #endregion
    
    public AppRunner(IEnumerable<string> args)
    {
        #region Process Arguments
        
        CliArguments.AddRange(args);

        if (CliArguments.Count == 0)
            AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml");
        
        if (CliArguments.Count == 1)
        {
            if (CliArguments[0] == "help")
            {
                HelpMode = true;
                return;
            }

            if (CliArguments[0] == "version")
            {
                VersionMode = true;
                return;
            }

            if (CliArguments[0] == "init")
            {
                InitMode = true;
                return;
            }

            var projectFilePath = CliArguments[0].SetNativePathSeparators();

            if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) && projectFilePath.Contains(Path.DirectorySeparatorChar))
            {
                AppState.YamlProjectFilePath = projectFilePath;
            }
            else
            {
                AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ? projectFilePath : $"fdeploy-{projectFilePath}.yml");
            }
        }

        #if DEBUG

        //AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Fynydd-Website-2024/UmbracoCms", "fdeploy-staging.yml");
        //AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/PentecHealthWebsite/Tolnedra", "fdeploy-staging.yml");
        //AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Coursabi/Coursabi.WebAPI", "fdeploy-staging.yml");
        AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Tolnedra2/UmbracoCms", "fdeploy-prod.yml");
        //AppState.YamlProjectFilePath = Path.Combine(@"c:\code\Fynydd-Website-2024\UmbracoCms", "fdeploy-staging.yml");
        
        #endif

        if (File.Exists(AppState.YamlProjectFilePath) == false)
        {
            HelpMode = true;
            return;
        }

        #endregion
        
        #region Load Settings
        
        if (File.Exists(AppState.YamlProjectFilePath) == false)
        {
            AppState.Exceptions.Add($"Could not find project file `{AppState.YamlProjectFilePath}`");
            AppState.CancellationTokenSource.Cancel();
            return;
        }

        if (AppState.YamlProjectFilePath.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            AppState.Exceptions.Add($"Invalid project file path `{AppState.YamlProjectFilePath}`");
            AppState.CancellationTokenSource.Cancel();
            return;
        }
            
        AppState.ProjectPath = AppState.YamlProjectFilePath[..AppState.YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(AppState.YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        AppState.Settings = deserializer.Deserialize<Settings>(yaml);

        if (AppState.Settings.MaxThreadCount < 1)
            AppState.Settings.MaxThreadCount = new Settings().MaxThreadCount;
        
        AppState.ProjectPath = AppState.YamlProjectFilePath[..AppState.YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];

        #region Credentials
        
        AppState.YamlCredsFilePath = Path.Combine(AppState.ProjectPath, "fdeploy-creds.yml");

        if (File.Exists(AppState.YamlCredsFilePath) == false)
        {
            AppState.YamlCredsFilePath = Path.Combine(AppState.ProjectPath, AppState.YamlProjectFilePath.GetLastPathSegment().Replace(".yml", "-creds.yml"));

            if (File.Exists(AppState.YamlCredsFilePath) == false)
            {
                AppState.YamlCredsFilePath = string.Empty;
            }
        }

        if (AppState.YamlCredsFilePath != string.Empty)
        {
            yaml = File.ReadAllText(AppState.YamlCredsFilePath);
            
            var credentials = deserializer.Deserialize<Credentials>(yaml);

            AppState.Settings.ServerConnection.Domain = credentials.Domain;
            AppState.Settings.ServerConnection.UserName = credentials.UserName;
            AppState.Settings.ServerConnection.Password = credentials.Password;
        }
        else
        {
            AppState.Exceptions.Add("No credentials file was found");
            AppState.CancellationTokenSource.Cancel();
            return;
        }

        #endregion
        
        #endregion

        #region Normalize Paths

        AppState.PublishPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Paths.PublishPath.SetNativePathSeparators()}";
        AppState.ProjectBinPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}bin";
        AppState.ProjectObjPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}obj";
        AppState.TrimmablePublishPath = AppState.PublishPath.MakeRelativePath();

        AppState.Settings.Project.ProjectFilePath = AppState.Settings.Project.ProjectFilePath.MakeRelativePath();
        AppState.Settings.ServerConnection.RemoteRootPath = AppState.Settings.ServerConnection.RemoteRootPath.MakeRelativePath();

        AppState.Settings.Project.CopyFilesToPublishFolder.NormalizePaths();
        AppState.Settings.Project.CopyFoldersToPublishFolder.NormalizePaths();

        AppState.Settings.Paths.OnlineCopyFilePaths.NormalizePaths();
        AppState.Settings.Paths.OnlineCopyFolderPaths.NormalizePaths();

        AppState.Settings.Paths.AlwaysOverwritePaths.NormalizePaths();
        AppState.Settings.Paths.AlwaysOverwritePathsWithRecurse.NormalizePaths();

        AppState.Settings.Paths.IgnoreFilePaths.NormalizePaths();
        AppState.Settings.Paths.IgnoreFolderPaths.NormalizePaths();

        var newList = new List<string>();
        
        foreach (var item in AppState.Settings.Project.CopyFilesToPublishFolder)
            newList.Add(item.MakeRelativePath().TrimStart(AppState.ProjectPath).MakeRelativePath());

        AppState.Settings.Project.CopyFilesToPublishFolder.Clear();
        AppState.Settings.Project.CopyFilesToPublishFolder.AddRange(newList);

        newList.Clear();
        
        foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
            newList.Add(item.MakeRelativePath().TrimStart(AppState.ProjectPath).MakeRelativePath());

        AppState.Settings.Project.CopyFoldersToPublishFolder.Clear();
        AppState.Settings.Project.CopyFoldersToPublishFolder.AddRange(newList);

        #endregion
    }
    
    #region Embedded Resources

    private async ValueTask<string> GetEmbeddedYamlPathAsync()
    {
        var workingPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

        while (workingPath.LastIndexOf(Path.DirectorySeparatorChar) > -1)
        {
            workingPath = workingPath[..workingPath.LastIndexOf(Path.DirectorySeparatorChar)];
            
#if DEBUG
            if (Directory.Exists(Path.Combine(workingPath, "yaml")) == false)
                continue;

            var tempPath = workingPath; 
			
            workingPath = Path.Combine(tempPath, "yaml");
#else
			if (Directory.Exists(Path.Combine(workingPath, "contentFiles")) == false)
				continue;
		
			var tempPath = workingPath; 

			workingPath = Path.Combine(tempPath, "contentFiles", "any", "any", "yaml");
#endif
            break;
        }

        // ReSharper disable once InvertIf
        if (string.IsNullOrEmpty(workingPath) || Directory.Exists(workingPath) == false)
        {
            AppState.Exceptions.Add("Embedded YAML resources cannot be found.");
            await AppState.CancellationTokenSource.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    private async ValueTask<string> GetEmbeddedHtmlPathAsync()
    {
        var workingPath = System.Reflection.Assembly.GetExecutingAssembly().Location;

        while (workingPath.LastIndexOf(Path.DirectorySeparatorChar) > -1)
        {
            workingPath = workingPath[..workingPath.LastIndexOf(Path.DirectorySeparatorChar)];
            
#if DEBUG
            if (Directory.Exists(Path.Combine(workingPath, "html")) == false)
                continue;

            var tempPath = workingPath; 
			
            workingPath = Path.Combine(tempPath, "html");
#else
			if (Directory.Exists(Path.Combine(workingPath, "contentFiles")) == false)
				continue;
		
			var tempPath = workingPath; 

			workingPath = Path.Combine(tempPath, "contentFiles", "any", "any", "html");
#endif
            break;
        }

        // ReSharper disable once InvertIf
        if (string.IsNullOrEmpty(workingPath) || Directory.Exists(workingPath) == false)
        {
            AppState.Exceptions.Add("Embedded HTML resources cannot be found.");
            await AppState.CancellationTokenSource.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    #endregion
    
    #region Console Output
    
    public void OutputExceptions()
    {
        foreach (var message in AppState.Exceptions)
            AnsiConsole.MarkupLine($"[red]{CliErrorPrefix}{message}[/]");            
    }

    public static void ColonOut(string topic, string message)
    {
        const int maxTopicLength = 20;
        var maxWidth = Console.WindowWidth - 23;

        message = message.TruncateCenter((int)Math.Floor(maxWidth / 3d), (int)Math.Floor((maxWidth / 3d) * 2) - 3, maxWidth);
        
        AnsiConsole.MarkupLine(topic.Length >= maxTopicLength
            ? $"[dim]{topic[..maxTopicLength]} :[/] {message}"
            : $"[dim]{topic}{" ".Repeat(maxTopicLength - topic.Length)} :[/] {message}");
    }
    
    #endregion
    
    #region Deployment
   
    public async ValueTask DeployAsync()
    {
        #region Process Modes
        
		var version = Identify.Version(System.Reflection.Assembly.GetExecutingAssembly());
        
		if (VersionMode)
		{
            AnsiConsole.MarkupLine($"[bold]Fdeploy[/] Version {version}");
			return;
		}
		
        AnsiConsole.Write(new Rule());
        AnsiConsole.MarkupLine("[bold]Fdeploy[/]: Deploy ASP.NET applications using SMB on Linux, macOS, or Windows");
        AnsiConsole.MarkupLine($"Version {version} for {Identify.GetOsPlatformName()} (.NET {Identify.GetRuntimeVersion()}/{Identify.GetProcessorArchitecture()})");
        AnsiConsole.Write(new Rule());
		
		if (InitMode)
		{
			var yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(), "fdeploy.yml"), AppState.CancellationTokenSource.Token);

            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
    			await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml"), yaml, AppState.CancellationTokenSource.Token);
			
            yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(), "fdeploy-creds.yml"), AppState.CancellationTokenSource.Token);

            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy-creds.yml"), yaml, AppState.CancellationTokenSource.Token);
            
            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
            {
                AnsiConsole.MarkupLine($"Created `fdeploy.yml` and `fdeploy-creds.yml` at {Directory.GetCurrentDirectory()}");
                AnsiConsole.MarkupLine(string.Empty);
    			return;
            }
		}
		else if (HelpMode)
		{
            AnsiConsole.MarkupLine(string.Empty);
            
            const string helpText = """
                                    Fdeploy will look in the current working directory for a deployment file named `fdeploy.yml`._
                                    You can also pass a path to a file named `fdeploy-{name}.yml` or even just pass the `{name}` portion which will look for a file named `fdeploy-{name}.yml`.

                                    Although you can put server credentials in the deployment file under the ServerConnection section, it is recommended that you instead use a separate credentials file and exclude it from your source code repository (e.g. git)._
                                    The name of the file can be either `fdeploy-creds.yml` which is used for all deployment files in a given project folder, or use a deployment filename with `-creds` at the end (e.g. `fdeploy-staging-creds.yml`).

                                    [bold]Command Line Usage:[/]
                                    """;

            const string exampleText = """
                                       fdeploy [[init|help|version]]
                                       fdeploy
                                       fdeploy {path to fdeploy-{name}.yml file}
                                       fdeploy {name}

                                       [bold]Commands:[/]
                                       """;

            const string commandsText = """
                                        init      [dim]:[/] Create starter `fdeploy.yml` and `fdeploy-creds.yml` files
                                                    in the current working directory
                                        version   [dim]:[/] Show the Fdeploy version number
                                        help      [dim]:[/] Show this help message
                                        """;

            helpText.WriteToConsole(80);
            AnsiConsole.MarkupLine(Strings.ThinLine.Repeat("Command Line Usage:".Length));
            exampleText.WriteToConsole(80);
            AnsiConsole.MarkupLine(Strings.ThinLine.Repeat("Commands:".Length));
            commandsText.WriteToConsole(80);
            
			AnsiConsole.MarkupLine(string.Empty);

			return;
		}

        ColonOut("Destination", $"{AppState.Settings.ServerConnection.ServerAddress}{Path.DirectorySeparatorChar}{AppState.Settings.ServerConnection.ShareName}{Path.DirectorySeparatorChar}{AppState.Settings.ServerConnection.RemoteRootPath}");
		ColonOut("Settings File", AppState.YamlProjectFilePath);
        
        if (AppState.YamlCredsFilePath != string.Empty)
            ColonOut("Credentials File", AppState.YamlCredsFilePath);
        
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion

        AppState.AppOfflineMarkup = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedHtmlPathAsync(), "AppOffline.html"), AppState.CancellationTokenSource.Token);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{MetaTitle}}", AppState.Settings.Offline.MetaTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageTitle}}", AppState.Settings.Offline.PageTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageHtml}}", AppState.Settings.Offline.ContentHtml);

        ColonOut("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");

        var sb = new StringBuilder();
        string prefix;
        var filesCopied = 0;
        var totalBytes = 0D;
        var filesRemoved = 0;
        var firstBlank = false;

        #region Connect To Server Share

        if (AppState.Settings.MountShare)
        {
            prefix = "Mounting network share...";

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(prefix, async ctx => {
                    
                    AppState.CurrentSpinner = ctx;

                    await AppState.ConnectNetworkShareAsync();

                });

            if (firstBlank == false)
            {
                firstBlank = true;
                AnsiConsole.MarkupLine(string.Empty);
            }

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
            
            AnsiConsole.MarkupLine($"[green]●[/] {prefix} [bold green]Success![/]");            
        }

        #endregion

        try
        {
            #region Delete Publish Folder

            if (Directory.Exists(AppState.PublishPath))
            {
                Timer.Restart();

                prefix = "Delete existing publish folder...";

                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .Start(prefix, ctx =>
                    {

                        AppState.CurrentSpinner = ctx;

                        var retries = AppState.Settings.RetryCount;

                        if (retries < 0)
                            retries = new Settings().RetryCount;

                        for (var x = 0; x < retries; x++)
                        {
                            try
                            {
                                Directory.Delete(AppState.PublishPath, true);

                                if (Directory.Exists(AppState.PublishPath) == false)
                                    break;
                            }
                            catch
                            {
                                for (var d = AppState.Settings.WriteRetryDelaySeconds; d >= 0; d--)
                                {
                                    ctx.Status($"{prefix} Retry {x + 1}");
                                    Thread.Sleep(1000);
                                }
                            }
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }

            #endregion

            #region Purge Bin Folder

            if (AppState.Settings.PurgeProject && Directory.Exists(AppState.ProjectBinPath))
            {
                Timer.Restart();
                
                prefix = "Purge bin folder...";

                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .Start(prefix, ctx =>
                    {
                        AppState.CurrentSpinner = ctx;

                        var retries = AppState.Settings.RetryCount;

                        if (retries < 0)
                            retries = new Settings().RetryCount;

                        for (var x = 0; x < retries; x++)
                        {
                            try
                            {
                                Directory.Delete(AppState.ProjectBinPath, true);
                                Directory.CreateDirectory(AppState.ProjectBinPath);
                                break;
                            }
                            catch
                            {
                                for (var d = AppState.Settings.WriteRetryDelaySeconds; d >= 0; d--)
                                {
                                    ctx.Status($"{prefix} Retry {x + 1}");
                                    Thread.Sleep(1000);
                                }
                            }
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }

            #endregion

            #region Purge Obj Folder

            if (AppState.Settings.PurgeProject && Directory.Exists(AppState.ProjectObjPath))
            {
                Timer.Restart();

                prefix = "Purge obj folder...";

                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .Start(prefix, ctx =>
                    {
                        AppState.CurrentSpinner = ctx;

                        var retries = AppState.Settings.RetryCount;

                        if (retries < 0)
                            retries = new Settings().RetryCount;

                        for (var x = 0; x < retries; x++)
                        {
                            try
                            {
                                Directory.Delete(AppState.ProjectObjPath, true);
                                Directory.CreateDirectory(AppState.ProjectObjPath);
                                break;
                            }
                            catch
                            {
                                for (var d = AppState.Settings.WriteRetryDelaySeconds; d >= 0; d--)
                                {
                                    ctx.Status($"{prefix} Retry {x + 1}");
                                    Thread.Sleep(1000);
                                }
                            }
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }

            #endregion

            #region Clean Project

            if (AppState.Settings.CleanProject)
            {
                prefix = $"Clean & restore project {AppState.Settings.Project.ProjectFileName}...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;

                        try
                        {
                            Timer.Restart();

                            var cmd = Cli.Wrap("dotnet")
                                .WithArguments(["restore", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}"])
                                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                                .WithStandardErrorPipe(PipeTarget.Null);
                            
                            var result = await cmd.ExecuteAsync();

                            if (result.IsSuccess == false)
                            {
                                AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                                AppState.Exceptions.Add($"Could not restore project packages; exit code: {result.ExitCode}");
                                await AppState.CancellationTokenSource.CancelAsync();
                                return;
                            }
                            
                            cmd = Cli.Wrap("dotnet")
                                .WithArguments(["clean", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}"])
                                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                                .WithStandardErrorPipe(PipeTarget.Null);
                            
                            result = await cmd.ExecuteAsync();

                            if (result.IsSuccess == false)
                            {
                                AppState.Exceptions.Add($"Could not clean the project; exit code: {result.ExitCode}");
                                await AppState.CancellationTokenSource.CancelAsync();
                            }
                        }
                        catch (Exception e)
                        {
                            AppState.Exceptions.Add($"Could not clean the project; {e.Message}");
                            await AppState.CancellationTokenSource.CancelAsync();
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }

            #endregion

            #region Publish Project
            
            prefix = $"Publishing project {AppState.Settings.Project.ProjectFileName}...";

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(prefix, async ctx =>
                {
                    AppState.CurrentSpinner = ctx;
                    
                    try
                    {
                        Timer.Restart();

                        var additionalParams = AppState.Settings.Project.PublishParameters.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                        var cliParams = new List<string>
                        {
                            "publish", "--framework", $"net{AppState.Settings.Project.TargetFramework:N1}", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}", "-c", AppState.Settings.Project.BuildConfiguration, "-o", AppState.PublishPath, $"/p:EnvironmentName={AppState.Settings.Project.EnvironmentName}"
                        };

                        cliParams.AddRange(additionalParams);
                    
                        var cmd = Cli.Wrap("dotnet")
                            .WithArguments(cliParams)
                            .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                            .WithStandardErrorPipe(PipeTarget.Null);
		        
                        var result = await cmd.ExecuteAsync();

                        if (result.IsSuccess == false)
                        {
                            AppState.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                            await AppState.CancellationTokenSource.CancelAsync();
                        }
                    }
                    catch (Exception e)
                    {
                        AppState.Exceptions.Add($"Could not publish the project; {e.Message}");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                });

            if (firstBlank == false)
            {
                firstBlank = true;
                AnsiConsole.MarkupLine(string.Empty);
            }

            if (AppState.CancellationTokenSource.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                return;
            }

            AnsiConsole.MarkupLine($"[green]●[/] {prefix} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");

            #endregion
            
            #region Copy Additional Files Into Publish Folder

            if (AppState.Settings.Project.CopyFilesToPublishFolder.Count != 0)
            {
                prefix = "Adding files to publish folder...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;

                        Timer.Restart();
                    
                        foreach (var item in AppState.Settings.Project.CopyFilesToPublishFolder)
                        {
                            var sourceFilePath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{item}";
                            var destFilePath = $"{AppState.PublishPath}{Path.DirectorySeparatorChar}{item}";
                            var destParentPath = destFilePath.TrimEnd(item.GetLastPathSegment()) ?? string.Empty;
                        
                            if (AppState.CancellationTokenSource.IsCancellationRequested)
                                break;
                        
                            try
                            {
                                Timer.Restart();

                                if (Directory.Exists(destParentPath) == false)
                                    Directory.CreateDirectory(destParentPath);
                            
                                File.Copy(sourceFilePath, destFilePath, true);
                            
                                ctx.Status($"{prefix} {item.GetLastPathSegment()}...");

                                await Task.Delay(5);
                            }

                            catch
                            {
                                AppState.Exceptions.Add($"Could not add file `{sourceFilePath} => {destFilePath}`");
                                await AppState.CancellationTokenSource.CancelAsync();
                            }
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} {AppState.Settings.Project.CopyFilesToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFilesToPublishFolder.Count.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }
            
            #endregion
            
            #region Copy Additional Folders Into Publish Folder
            
            if (AppState.Settings.Project.CopyFoldersToPublishFolder.Count != 0)
            {
                prefix = "Adding folders to publish folder...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;
                        
                        Timer.Restart();
                    
                        foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
                        {
                            if (AppState.CancellationTokenSource.IsCancellationRequested)
                                break;
                        
                            try
                            {
                                Timer.Restart();

                                await Storage.CopyLocalFolderAsync(AppState, $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{item}", $"{AppState.PublishPath}{Path.DirectorySeparatorChar}{item}");

                                ctx.Status($"{prefix} {item.GetLastPathSegment()}...");
                                await Task.Delay(5);
                            }

                            catch
                            {
                                AppState.Exceptions.Add($"Could not add folder `{item}`");
                                await AppState.CancellationTokenSource.CancelAsync();
                            }
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count.Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }
            
            #endregion
            
            #region Index Local Files

            prefix = "Indexing local files...";

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync(prefix, async ctx =>
                {
                    AppState.CurrentSpinner = ctx;
                        
                    Timer.Restart();
                
                    await Storage.RecurseLocalPathAsync(AppState, AppState.PublishPath, prefix);
                });

            if (firstBlank == false)
            {
                firstBlank = true;
                AnsiConsole.MarkupLine(string.Empty);
            }
            
            if (AppState.CancellationTokenSource.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                return;
            }

            AnsiConsole.MarkupLine($"[green]●[/] {prefix} {AppState.LocalFiles.Count(f => f.IsFile):N0} {AppState.LocalFiles.Count(f => f.IsFile).Pluralize("file", "files")}, {AppState.LocalFiles.Count(f => f.IsFolder):N0} {AppState.LocalFiles.Count(f => f.IsFolder).Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            
            #endregion

            #region Deploy Files While Online

            if (AppState.Settings.Paths.OnlineCopyFolderPaths.Count > 0 || AppState.Settings.Paths.OnlineCopyFilePaths.Count > 0)
            {
                Timer.Restart();

                prefix = "Deploy files (server online)...";
                filesCopied = 0;
                totalBytes = 0D;

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;
                        
                        var filesToCopy = AppState.LocalFiles.Where(f => f is { IsFile: true, IsOnlineCopy: true }).OrderBy(f => f.RelativeComparablePath).ToList();

                        const int groupSize = 10;

                        // Threads of `groupSize` items to copy concurrently
                        var arrayOfLists = filesToCopy
                            .Select((item, index) => new { Item = item, Index = index })
                            .GroupBy(x => x.Index / groupSize)
                            .Select(g => g.Select(x => x.Item).ToList())
                            .ToArray();
                        
                        var tasks = new List<Task>();
                        var semaphore = new SemaphoreSlim(AppState.Settings.MaxThreadCount);

                        foreach (var group in arrayOfLists)
                        {
                            await semaphore.WaitAsync();

                            tasks.Add(Task.Run(() =>
                            {
                                try
                                {
                                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                                        return;

                                    foreach (var fo in group)
                                    {
                                        if (fo.AlwaysOverwrite == false)
                                        {
                                            if (File.Exists(fo.AbsoluteServerPath))
                                            {
                                                var fileInfo = new FileInfo(fo.AbsoluteServerPath);

                                                if ((AppState.Settings.CompareFileDates == false ||
                                                     fileInfo.LastWriteTime.ComparableTime() == fo.LastWriteTime) &&
                                                    (AppState.Settings.CompareFileSizes == false ||
                                                     fileInfo.Length == fo.FileSizeBytes))
                                                {
                                                    if (AppState.CurrentSpinner is not null)
                                                    {
                                                        if (AppState.CurrentSpinner.Status != $"{prefix} Scanning...")
                                                            AppState.CurrentSpinner.Status($"{prefix} Scanning...");
                                                    }

                                                    continue;
                                                }
                                            }
                                        }

                                        ctx.Status($"{prefix} {fo.FileNameOrPathSegment}...");
                                        totalBytes += fo.FileSizeBytes;
                                        AppState.CopyFile(fo, prefix);
                                        filesCopied++;
                                    }
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }));
                        }

                        await Task.WhenAll(tasks);
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                var bps = totalBytes / Timer.Elapsed.TotalSeconds;

                AnsiConsole.MarkupLine(filesCopied != 0 ? 
                    $"[green]●[/] {prefix} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} copied ({Timer.Elapsed.FormatElapsedTime()}, {bps.FormatBytes()}/sec)... [bold green]Success![/]" : 
                    $"[green]●[/] {prefix} Nothing to copy... [bold green]Success![/]");
            }

            #endregion

            #region Take Server Offline

            var offlineTimer = new Stopwatch();

            if (AppState.Settings.TakeServerOffline)
            {
                offlineTimer.Start();

                prefix = "Take website offline...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;
                        
                        AppState.TakeServerOffline(prefix);

                        if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                        {
                            if (AppState.Settings.ServerOfflineDelaySeconds > 0)
                            {
                                for (var i = AppState.Settings.ServerOfflineDelaySeconds; i >= 0; i--)
                                {
                                    ctx.Status($"{prefix} Done... Waiting ({i:N0})");

                                    await Task.Delay(1000);
                                }
                            }
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }
                
                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} [bold green]Success![/]");
            }

            #endregion

            #region Deploy Files While Offline

            Timer.Restart();
            
            var filesToCopy = AppState.LocalFiles.Where(f => f is { IsFile: true, IsOnlineCopy: false }).OrderBy(f => f.RelativeComparablePath).ToList();
            var fileCount = filesToCopy.Count;
            
            totalBytes = 0;
            filesCopied = 0;

            prefix = "Deploy files (server offline)...";

            if (fileCount > 0)
            {
                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;

                        const int groupSize = 10;

                        // Threads of `groupSize` items to copy concurrently
                        var arrayOfLists = filesToCopy
                            .Select((item, index) => new { Item = item, Index = index })
                            .GroupBy(x => x.Index / groupSize)
                            .Select(g => g.Select(x => x.Item).ToList())
                            .ToArray();

                        var tasks = new List<Task>();
                        var semaphore = new SemaphoreSlim(AppState.Settings.MaxThreadCount);

                        foreach (var group in arrayOfLists)
                        {
                            await semaphore.WaitAsync();

                            tasks.Add(Task.Run(() =>
                            {
                                try
                                {
                                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                                        return;

                                    foreach (var fo in group)
                                    {
                                        if (fo.AlwaysOverwrite == false)
                                        {
                                            if (File.Exists(fo.AbsoluteServerPath))
                                            {
                                                var fileInfo = new FileInfo(fo.AbsoluteServerPath);

                                                if ((AppState.Settings.CompareFileDates == false ||
                                                     fileInfo.LastWriteTime.ComparableTime() == fo.LastWriteTime) &&
                                                    (AppState.Settings.CompareFileSizes == false ||
                                                     fileInfo.Length == fo.FileSizeBytes))
                                                    continue;
                                            }
                                        }

                                        totalBytes += fo.FileSizeBytes;

                                        AppState.CopyFile(fo, prefix);

                                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                                            return;

                                        filesCopied++;
                                    }
                                }
                                finally
                                {
                                    semaphore.Release();
                                }
                            }));
                        }

                        await Task.WhenAll(tasks);
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }
                
                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                var bps = totalBytes / Timer.Elapsed.TotalSeconds;

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} copied ({Timer.Elapsed.FormatElapsedTime()}, {bps.FormatBytes()}/sec)... [bold green]Success![/]");
            }
            else
            {
                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} No files to update... [bold green]Success![/]");
            }

            #endregion

            #region Index Server Files

            if (AppState.Settings.DeleteOrphans)
            {
                prefix = "Indexing server files for cleanup...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;
                        
                        Timer.Restart();
                
                        await AppState.RecurseServerPathAsync(AppState.GetServerPathPrefix(), prefix);
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }
                
                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} {AppState.ServerFiles.Count(f => f.IsFile):N0} {AppState.ServerFiles.Count(f => f.IsFile).Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }

            #endregion
            
            #region Process Orphan Deletions

            if (AppState.Settings.DeleteOrphans)
            {
                prefix = "Deleting orphaned files...";

                AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .Start(prefix, ctx =>
                    {
                        AppState.CurrentSpinner = ctx;
                        
                        Timer.Restart();
                
                        var itemsToDelete = AppState.ServerFiles.Except(AppState.LocalFiles, new FileObjectComparer()).ToList();

                        // Remove paths that enclose ignore paths
                        foreach (var fileObject in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                        {
                            var item = (ServerFileObject)fileObject;
                            
                            foreach (var ignorePath in AppState.Settings.Paths.IgnoreFolderPaths)
                            {
                                if (ignorePath.StartsWith(item.RelativeComparablePath) == false)
                                    continue;

                                itemsToDelete.Remove(item);
                            }
                        }

                        // Remove descendants of folders to be deleted
                        foreach (var fileObject in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                        {
                            var item = (ServerFileObject)fileObject;

                            foreach (var subitem in itemsToDelete.ToList().OrderByDescending(o => o.Level))
                            {
                                if (subitem.Level > item.Level && subitem.RelativeComparablePath.StartsWith(item.RelativeComparablePath))
                                    itemsToDelete.Remove(subitem);
                            }
                        }

                        foreach (var fileObject in itemsToDelete)
                        {
                            var item = (ServerFileObject)fileObject;

                            if (item.IsFile)
                            {
                                AppState.DeleteServerFile(item, prefix, true);
                                filesRemoved++;
                            }
                            else
                            {
                                AppState.DeleteServerFolder(item, prefix, true);
                            }

                            if (AppState.CancellationTokenSource.IsCancellationRequested)
                                break;
                        }
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }
                
                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} {filesRemoved:N0} {filesRemoved.Pluralize("file", "files")} deleted ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");
            }

            #endregion
            
            #region Bring Server Online

            if (AppState.Settings.TakeServerOffline)
            {
                prefix = "Bring website online...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;
                        
                        if (AppState.Settings.ServerOnlineDelaySeconds > 0)
                        {
                            for (var i = AppState.Settings.ServerOnlineDelaySeconds; i >= 0; i--)
                            {
                                ctx.Status($"{prefix}... Waiting ({i:N0})");
                                await Task.Delay(1000);
                            }
                        }

                        AppState.BringServerOnline(prefix);
                    });

                if (firstBlank == false)
                {
                    firstBlank = true;
                    AnsiConsole.MarkupLine(string.Empty);
                }
                
                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]●[/] {prefix} Website offline for {offlineTimer.Elapsed.FormatElapsedTime()}... [bold green]Success![/]");
            }

            #endregion

            #region Local Cleanup

            prefix = "Cleaning up...";

            AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .Start(prefix, ctx =>
                {
                    AppState.CurrentSpinner = ctx;
                    Timer.Restart();
                
                    var retries = AppState.Settings.RetryCount;

                    if (retries < 0)
                        retries = new Settings().RetryCount;

                    for (var x = 0; x < retries; x++)
                    {
                        try
                        {
                            Directory.Delete(AppState.PublishPath, true);
                            break;
                        }
                        catch
                        {
                            for (var d = AppState.Settings.WriteRetryDelaySeconds; d >= 0; d--)
                            {
                                ctx.Status($"{prefix} Retry {x + 1}");
                                Thread.Sleep(1000);
                            }
                        }
                    }
                });

            if (firstBlank == false)
            {
                firstBlank = true;
                AnsiConsole.MarkupLine(string.Empty);
            }
            
            if (AppState.CancellationTokenSource.IsCancellationRequested)
            {
                AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                return;
            }

            AnsiConsole.MarkupLine($"[green]●[/] {prefix} ({Timer.Elapsed.FormatElapsedTime()})... [bold green]Success![/]");

            #endregion
        }
        finally
        {
            if (AppState.Settings.UnmountShare)
            {
                var result = false;
                
                prefix = "Flushing caches and unmounting network share...";

                await AnsiConsole.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync(prefix, async ctx =>
                    {
                        AppState.CurrentSpinner = ctx;

                        result = await AppState.DisconnectNetworkShareAsync();
                    });

                if (firstBlank == false)
                    AnsiConsole.MarkupLine(string.Empty);
                
                if (AppState.CancellationTokenSource.IsCancellationRequested || result == false)
                    AnsiConsole.MarkupLine($"[red]{prefix} Failed![/]");
                else
                    AnsiConsole.MarkupLine($"[green]●[/] {prefix} [bold green]Success![/]");
            }
        }
    }
    
    #endregion
}