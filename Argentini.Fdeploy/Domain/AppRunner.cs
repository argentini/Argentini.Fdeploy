using Argentini.Fdeploy.ConsoleBusy;
using YamlDotNet.Serialization;

namespace Argentini.Fdeploy.Domain;

public sealed class AppRunner
{
    #region Constants

    public static int MaxConsoleWidth => GetMaxConsoleWidth();
	
    private static int GetMaxConsoleWidth()
    {
        try
        {
            return Console.WindowWidth - 1;
        }
        catch
        {
            return 78;
        }
    }

    public static string CliErrorPrefix => "  • ";

    #endregion

    #region Run Mode Properties

    public bool VersionMode { get; set; }
    public bool InitMode { get; set; }
    public bool HelpMode { get; set; }

    #endregion
    
    #region App State Properties

    public List<string> CliArguments { get; } = [];
    public AppState AppState { get; } = new();

    #endregion
    
    #region Properties
    
    public Stopwatch Timer { get; } = new();

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
                if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), projectFilePath);
                else
                    AppState.YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), $"fdeploy-{projectFilePath}.yml");
            }
        }

        #if DEBUG

        //AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/PentecHealthWebsite/Tolnedra", "fdeploy-staging.yml");
        AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Fynydd-Website-2024/UmbracoCms", "fdeploy-staging.yml");
        //AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Tolnedra2/UmbracoCms", "fdeploy-prod.yml");
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

        #endregion
        
        #endregion

        #region Normalize Paths

        AppState.PublishPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
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
    
    public async ValueTask<string> GetEmbeddedYamlPathAsync()
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

    public async ValueTask<string> GetEmbeddedHtmlPathAsync()
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
    
    public async ValueTask OutputExceptionsAsync()
    {
        foreach (var message in AppState.Exceptions)
            await Console.Out.WriteLineAsync($"{CliErrorPrefix}{message}");
    }

    public static async ValueTask ColonOutAsync(string topic, string message)
    {
        const int maxTopicLength = 20;

        if (topic.Length >= maxTopicLength)
            await Console.Out.WriteAsync($"{topic[..maxTopicLength]}");
        else
            await Console.Out.WriteAsync($"{topic}{" ".Repeat(maxTopicLength - topic.Length)}");
        
        await Console.Out.WriteLineAsync($" : {message}");
    }
    
    #endregion
    
    #region Deployment
   
    public async ValueTask DeployAsync()
    {
        #region Process Modes
        
		var version = await Identify.VersionAsync(System.Reflection.Assembly.GetExecutingAssembly());
        
		if (VersionMode)
		{
			await Console.Out.WriteLineAsync($"Fdeploy Version {version}");
			return;
		}
		
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(MaxConsoleWidth));
		await Console.Out.WriteLineAsync("Fdeploy: Deploy .NET web applications using SMB on Linux, macOS, or Windows");
		await Console.Out.WriteLineAsync($"Version {version} for {Identify.GetOsPlatformName()} (.NET {Identify.GetRuntimeVersion()}/{Identify.GetProcessorArchitecture()})");
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(MaxConsoleWidth));
		
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
			    await Console.Out.WriteLineAsync($"Created `fdeploy.yml` and `fdeploy-creds.yml` at {Directory.GetCurrentDirectory()}");
			    await Console.Out.WriteLineAsync();
    			return;
            }
		}
		
		else if (HelpMode)
		{
			await Console.Out.WriteLineAsync();
            
            const string helpText = """
                                    Fdeploy will look in the current working directory for a deployment file named `fdeploy.yml`._
                                    You can also pass a path to a file named `fdeploy-{name}.yml` or even just pass the `{name}` portion which will look for a file named `fdeploy-{name}.yml`.

                                    Although you can put server credentials in the deployment file under the ServerConnection section, it is recommended that you instead use a separate credentials file and exclude it from your source code repository (e.g. git)._
                                    The name of the file can be either `fdeploy-creds.yml` which is used for all deployment files in a given project folder, or use a deployment filename with `-creds` at the end (e.g. `fdeploy-staging-creds.yml`).

                                    Command Line Usage:
                                    """;

            const string exampleText = """
                                       fdeploy [init|help|version]
                                       fdeploy
                                       fdeploy {path to fdeploy-{name}.yml file}
                                       fdeploy {name}

                                       Commands:
                                       """;

            const string commandsText = """
                                        init      : Create starter `fdeploy.yml` and `fdeploy-creds.yml` files in the
                                                  : current working directory
                                        version   : Show the Fdeploy version number
                                        help      : Show this help message
                                        """;

            helpText.WriteToConsole(80);
            await Console.Out.WriteLineAsync(Strings.ThinLine.Repeat("Command Line Usage:".Length));
            exampleText.WriteToConsole(80);
            await Console.Out.WriteLineAsync(Strings.ThinLine.Repeat("Commands:".Length));
            commandsText.WriteToConsole(80);
            
			await Console.Out.WriteLineAsync();

			return;
		}

        await ColonOutAsync("Destination", $"{AppState.Settings.ServerConnection.ServerAddress}{Path.DirectorySeparatorChar}{AppState.Settings.ServerConnection.ShareName}{Path.DirectorySeparatorChar}{AppState.Settings.ServerConnection.RemoteRootPath}");
		await ColonOutAsync("Settings File", AppState.YamlProjectFilePath);
        
        if (AppState.YamlCredsFilePath != string.Empty)
            await ColonOutAsync("Credentials File", AppState.YamlCredsFilePath);
        
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion

        AppState.AppOfflineMarkup = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedHtmlPathAsync(), "AppOffline.html"), AppState.CancellationTokenSource.Token);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{MetaTitle}}", AppState.Settings.Offline.MetaTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageTitle}}", AppState.Settings.Offline.PageTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageHtml}}", AppState.Settings.Offline.ContentHtml);

        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();

        var sb = new StringBuilder();

        #region Connect To Server Share

        if (AppState.Settings.MountShare)
        {
            await Spinner.StartAsync("Mounting network share...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                if (await AppState.ConnectNetworkShareAsync())
                    spinner.Succeed($"{spinner.OriginalText} Success!");

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }

        #endregion
        
        #region Delete Publish Folder

        if (Directory.Exists(AppState.PublishPath))
        {
            Timer.Restart();

            await Spinner.StartAsync("Delete existing publish folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

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
                            spinner.Text = $"{spinner.OriginalText} Retry {x + 1}";
                            Thread.Sleep(1000);
                        }
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinner.OriginalText} Failed!");
                    return;
                }

                spinner.Text = $"{spinner.RootText} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion

        #region Clean Project

        if (AppState.Settings.CleanProject)
        {
            await Spinner.StartAsync($"Clean project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
            {
                try
                {
                    Timer.Restart();

                    var cmd = Cli.Wrap("dotnet")
                        .WithArguments(new [] { "clean", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}" })
                        .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                        .WithStandardErrorPipe(PipeTarget.Null);
                    
                    var result = await cmd.ExecuteAsync();

                    if (result.IsSuccess == false)
                    {
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                        AppState.Exceptions.Add($"Could not clean the project; exit code: {result.ExitCode}");
                        await AppState.CancellationTokenSource.CancelAsync();
                        return;
                    }
                    
                    spinner.Text = $"{spinner.RootText} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                }

                catch (Exception e)
                {
                    spinner.Fail($"{spinner.OriginalText}... Failed!");
                    AppState.Exceptions.Add($"Could not clean the project; {e.Message}");
                    await AppState.CancellationTokenSource.CancelAsync();
                }
                
            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }

        #endregion

        #region Purge Bin Folder

        if (AppState.Settings.PurgeProject && Directory.Exists(AppState.ProjectBinPath))
        {
            Timer.Restart();
            
            await Spinner.StartAsync("Purge bin folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

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
                            spinner.Text = $"{spinner.OriginalText} Retry {x + 1}";
                            Thread.Sleep(1000);
                        }
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinner.OriginalText} Failed!");
                    return;
                }

                spinner.Text = $"{spinner.RootText} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion

        #region Purge Obj Folder

        if (AppState.Settings.PurgeProject && Directory.Exists(AppState.ProjectObjPath))
        {
            Timer.Restart();
            
            await Spinner.StartAsync("Purge obj folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

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
                            spinner.Text = $"{spinner.OriginalText} Retry {x + 1}";
                            Thread.Sleep(1000);
                        }
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinner.OriginalText} Failed!");
                    return;
                }

                spinner.Text = $"{spinner.RootText} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                
                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion
        
        #region Publish Project
        
        await Spinner.StartAsync($"Publishing project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                Timer.Restart();

                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{AppState.Settings.Project.TargetFramework:N1}", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}", "-c", AppState.Settings.Project.BuildConfiguration, "-o", AppState.PublishPath, $"/p:EnvironmentName={AppState.Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"{spinner.OriginalText} Failed!");
                    AppState.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await AppState.CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"{spinner.RootText} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
            }

            catch (Exception e)
            {
                spinner.Fail($"{spinner.OriginalText} Failed!");
                AppState.Exceptions.Add($"Could not publish the project; {e.Message}");
                await AppState.CancellationTokenSource.CancelAsync();
            }
            
        }, Patterns.Dots, Patterns.Line);

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion
        
        #region Copy Additional Files Into Publish Folder

        if (AppState.Settings.Project.CopyFilesToPublishFolder.Count != 0)
        {
            await Spinner.StartAsync("Adding files to publish folder...", async spinner =>
            {
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
                        
                        spinner.Text = $"{spinner.OriginalText} {item.GetLastPathSegment()}...";

                        await Task.Delay(5);
                    }

                    catch
                    {
                        spinner.Fail($"{spinner.OriginalText} {item.GetLastPathSegment()}... Failed!");
                        AppState.Exceptions.Add($"Could not add file `{sourceFilePath} => {destFilePath}`");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Text = $"{spinner.OriginalText} {AppState.Settings.Project.CopyFilesToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFilesToPublishFolder.Count.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }
        
        #endregion
        
        #region Copy Additional Folders Into Publish Folder
        
        if (AppState.Settings.Project.CopyFoldersToPublishFolder.Count != 0)
        {
            await Spinner.StartAsync("Adding folders to publish folder...", async spinner =>
            {
                Timer.Restart();
                
                foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
                {
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        break;
                    
                    try
                    {
                        Timer.Restart();

                        await Storage.CopyLocalFolderAsync(AppState, $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{item}", $"{AppState.PublishPath}{Path.DirectorySeparatorChar}{item}");

                        spinner.Text = $"{spinner.OriginalText} {item.GetLastPathSegment()}...";
                        await Task.Delay(5);
                    }

                    catch
                    {
                        spinner.Fail($"{spinner.OriginalText} {item.GetLastPathSegment()}... Failed!");
                        AppState.Exceptions.Add($"Could not add folder `{item}`");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Text = $"{spinner.OriginalText} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count.Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            Timer.Restart();
            
            await Storage.RecurseLocalPathAsync(AppState, AppState.PublishPath);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail($"{spinner.OriginalText} Failed!");
            else        
                spinner.Text = $"{spinner.OriginalText} {AppState.LocalFiles.Count(f => f.IsFile):N0} {AppState.LocalFiles.Count(f => f.IsFile).Pluralize("file", "files")}, {AppState.LocalFiles.Count(f => f.IsFolder):N0} {AppState.LocalFiles.Count(f => f.IsFolder).Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        try
        {
            #region Deploy Files While Online

            if (AppState.Settings.Paths.OnlineCopyFolderPaths.Count > 0 || AppState.Settings.Paths.OnlineCopyFilePaths.Count > 0)
            {
                Timer.Restart();

                await Spinner.StartAsync("Deploy files (server online)...", async spinner =>
                {
                    var filesCopied = 0;
                    var totalBytes = 0D;

                    AppState.CurrentSpinner = spinner;

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
                                            
                                            if ((AppState.Settings.CompareFileDates == false || fileInfo.LastWriteTime.ComparableTime() == fo.LastWriteTime) && (AppState.Settings.CompareFileSizes == false || fileInfo.Length == fo.FileSizeBytes))
                                            {
                                                if (AppState.CurrentSpinner is not null)
                                                {
                                                    if (AppState.CurrentSpinner.Text != $"{AppState.CurrentSpinner.OriginalText} Scanning...")
                                                        AppState.CurrentSpinner.Text = $"{AppState.CurrentSpinner.OriginalText} Scanning...";
                                                }
                                                
                                                continue;
                                            }
                                        }
                                    }
                    
                                    spinner.Text = $"{spinner.OriginalText} {fo.FileNameOrPathSegment}...";
                                    totalBytes += fo.FileSizeBytes;
                                    AppState.CopyFile(fo);
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
                    
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                    }
                    else
                    {
                        var bps = totalBytes / Timer.Elapsed.TotalSeconds;
                        
                        if (filesCopied != 0)
                            spinner.Text = $"{spinner.OriginalText} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} copied ({Timer.Elapsed.FormatElapsedTime()}, {bps.FormatBytes()}/sec)... Success!";
                        else
                            spinner.Text = $"{spinner.OriginalText} Nothing to copy... Success!";
                    }

                    await Task.CompletedTask;

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Take Server Offline

            var offlineTimer = new Stopwatch();

            if (AppState.Settings.TakeServerOffline)
            {
                offlineTimer.Start();

                await Spinner.StartAsync("Take website offline...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    AppState.TakeServerOffline();

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                    }
                    else
                    {
                        if (AppState.Settings.ServerOfflineDelaySeconds > 0)
                        {
                            for (var i = AppState.Settings.ServerOfflineDelaySeconds; i >= 0; i--)
                            {
                                spinner.Text = $"{spinner.OriginalText} Done... Waiting ({i:N0})";

                                await Task.Delay(1000);
                            }
                        }

                        spinner.Text = $"{spinner.OriginalText} Success!";
                    }

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Deploy Files While Offline

            Timer.Restart();
            
            var filesToCopy = AppState.LocalFiles.Where(f => f is { IsFile: true, IsOnlineCopy: false }).OrderBy(f => f.RelativeComparablePath).ToList();
            var fileCount = filesToCopy.Count;

            await Spinner.StartAsync("Deploy files (server offline)...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var totalBytes = 0D;
                var filesCopied = 0;

                if (fileCount > 0)
                {
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

                                            if ((AppState.Settings.CompareFileDates == false || fileInfo.LastWriteTime.ComparableTime() == fo.LastWriteTime) && (AppState.Settings.CompareFileSizes == false || fileInfo.Length == fo.FileSizeBytes))
                                                continue;
                                        }
                                    }
                                    
                                    totalBytes += fo.FileSizeBytes;

                                    AppState.CopyFile(fo);

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

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                    else
                    {
                        var bps = totalBytes / Timer.Elapsed.TotalSeconds;

                        spinner.Text = $"{spinner.OriginalText} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} copied ({Timer.Elapsed.FormatElapsedTime()}, {bps.FormatBytes()}/sec)... Success!";
                    }
                }
                else
                {
                    spinner.Text = $"{spinner.OriginalText} No files to update... Success!";
                }

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Index Server Files

            if (AppState.Settings.DeleteOrphans)
            {
                await Spinner.StartAsync("Indexing server files for cleanup...", async spinner =>
                {
                    Timer.Restart();
                    AppState.CurrentSpinner = spinner;

                    await AppState.RecurseServerPathAsync(AppState.GetServerPathPrefix());

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                    else
                        spinner.Text = $"{spinner.OriginalText} {AppState.ServerFiles.Count(f => f.IsFile):N0} {AppState.ServerFiles.Count(f => f.IsFile).Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                });
                
                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion
            
            #region Process Orphan Deletions

            if (AppState.Settings.DeleteOrphans)
            {
                await Spinner.StartAsync("Deleting orphaned files...", async spinner =>
                {
                    Timer.Restart();

                    var filesRemoved = 0;

                    AppState.CurrentSpinner = spinner;
                    
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
                            AppState.DeleteServerFile(item, true);
                            filesRemoved++;
                        }
                        else
                        {
                            AppState.DeleteServerFolder(item, true);
                        }

                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                            break;
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                    else
                        spinner.Text = $"{spinner.OriginalText} {filesRemoved:N0} {filesRemoved.Pluralize("file", "files")} deleted ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                    
                    await Task.CompletedTask;

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion
            
            #region Bring Server Online

            if (AppState.Settings.TakeServerOffline)
            {
                await Spinner.StartAsync("Bring website online...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    if (AppState.Settings.ServerOnlineDelaySeconds > 0)
                    {
                        for (var i = AppState.Settings.ServerOnlineDelaySeconds; i >= 0; i--)
                        {
                            spinner.Text = $"{spinner.OriginalText}... Waiting ({i:N0})";
                            await Task.Delay(1000);
                        }
                    }

                    AppState.BringServerOnline();
                    
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinner.OriginalText} Failed!");
                    }
                    else
                    {
                        spinner.Text = $"{spinner.OriginalText} Success!";
                    }

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;

                await Spinner.StartAsync("Website offline for ", async spinner =>
                {
                    spinner.Text += offlineTimer.Elapsed.FormatElapsedTime();

                    await Task.CompletedTask;

                }, Patterns.Dots, Patterns.Line);
            }

            #endregion

            #region Local Cleanup

            await Spinner.StartAsync("Cleaning up...", async spinner =>
            {
                Timer.Restart();
                
                AppState.CurrentSpinner = spinner;

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
                            spinner.Text = $"{spinner.OriginalText} Retry {x + 1}";
                            Thread.Sleep(1000);
                        }
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinner.OriginalText} Failed!");
                else
                    spinner.Text = $"{spinner.RootText} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            #endregion
        }
        finally
        {
            if (AppState.Settings.UnmountShare)
            {
                await Spinner.StartAsync("Flushing caches and unmounting network share...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    if (await AppState.DisconnectNetworkShareAsync())
                        spinner.Succeed($"{spinner.OriginalText} Success!");

                    await Task.CompletedTask;

                }, Patterns.Dots, Patterns.Line);
            }
        }
    }
    
    #endregion
}