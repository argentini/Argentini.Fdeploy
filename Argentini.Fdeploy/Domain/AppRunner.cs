using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary.Client;
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
    public AppState AppState { get; }

    #endregion
    
    #region Properties
    
    public Stopwatch Timer { get; } = new();

    #endregion
    
    public AppRunner(IEnumerable<string> args)
    {
        AppState = new AppState
        {
            Client = new SMB2Client()
        };

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

        AppState.YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Fynydd-Website-2024/UmbracoCms", "fdeploy-staging.yml");
        
        #endif

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
            
        AppState.WorkingPath = AppState.YamlProjectFilePath[..AppState.YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(AppState.YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        AppState.Settings = deserializer.Deserialize<Settings>(yaml);
        
        #endregion

        #region Normalize Paths

        AppState.Settings.Project.ProjectFilePath = AppState.Settings.Project.ProjectFilePath.NormalizePath();
        AppState.Settings.Paths.RemoteRootPath = AppState.Settings.Paths.RemoteRootPath.NormalizePath();

        AppState.Settings.Paths.StaticFilePaths.NormalizePaths();
        AppState.Settings.Paths.RelativeIgnorePaths.NormalizePaths();
        AppState.Settings.Paths.RelativeIgnoreFilePaths.NormalizePaths();

        foreach (var copy in AppState.Settings.Paths.StaticFileCopies)
        {
            copy.Source = copy.Source.NormalizePath();
            copy.Destination = copy.Destination.NormalizePath();
        }
        
        foreach (var copy in AppState.Settings.Paths.FileCopies)
        {
            copy.Source = copy.Source.NormalizePath();
            copy.Destination = copy.Destination.NormalizePath();
        }
        
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
		Timer.Start();
		
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
			
            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
            {
			    await Console.Out.WriteLineAsync($"Created fdeploy.yml file at {Directory.GetCurrentDirectory()}");
			    await Console.Out.WriteLineAsync();
    			return;
            }
		}
		
		else if (HelpMode)
		{
			await Console.Out.WriteLineAsync();
			await Console.Out.WriteLineAsync("Fdeploy will look in the current working directory for a file named `fdeploy.yml`.");
			await Console.Out.WriteLineAsync("You can also pass a path to a file named `fdeploy-{name}.yml` or even just pass");
			await Console.Out.WriteLineAsync("the `{name}` portion which will look for a file named `fdeploy-{name}.yml` in the");
            await Console.Out.WriteLineAsync("current working directory.");
			await Console.Out.WriteLineAsync();
			await Console.Out.WriteLineAsync("Command Line Usage:");
			await Console.Out.WriteLineAsync(Strings.ThinLine.Repeat("Command Line Usage:".Length));
			await Console.Out.WriteLineAsync("fdeploy [init|help|version]");
			await Console.Out.WriteLineAsync("fdeploy");
            await Console.Out.WriteLineAsync("fdeploy {path to fdeploy-{name}.yml file}");
            await Console.Out.WriteLineAsync("fdeploy {name}");
			await Console.Out.WriteLineAsync();
			await Console.Out.WriteLineAsync("Commands:");
			await Console.Out.WriteLineAsync(Strings.ThinLine.Repeat("Commands:".Length));
			await Console.Out.WriteLineAsync("init      : Create starter `fdeploy.yml` in the current working directory");
			await Console.Out.WriteLineAsync("version   : Show the Fdeploy version number");
			await Console.Out.WriteLineAsync("help      : Show this help message");
			await Console.Out.WriteLineAsync();

			return;
		}

		await ColonOutAsync("Settings File", AppState.YamlProjectFilePath);

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion

        AppState.AppOfflineMarkup = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedHtmlPathAsync(), "AppOffline.html"), AppState.CancellationTokenSource.Token);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{MetaTitle}}", AppState.Settings.Offline.MetaTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageTitle}}", AppState.Settings.Offline.PageTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageHtml}}", AppState.Settings.Offline.PageHtml);

        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();
        
        #region Connect to Server

        await Spinner.StartAsync($"Connecting to {AppState.Settings.ServerConnection.ServerAddress}...", spinner =>
        {
            Storage.Connect(AppState);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
            {
                spinner.Fail($"Connecting to {AppState.Settings.ServerConnection.ServerAddress}... Failed!");
                Storage.Disconnect(AppState);
            
                return Task.CompletedTask;
            }

            spinner.Text = $"Connecting to {AppState.Settings.ServerConnection.ServerAddress}... Success!";
            
            return Task.CompletedTask;
            
        }, Patterns.Dots, Patterns.Line);

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        #region Test Code
        
        // await Spinner.StartAsync("Test copy...", async spinner =>
        // {
        //     AppState.Spinner = spinner;
        //     
        //     await Smb.CopyFileAsync(AppState, "/Users/magic/Developer/Argentini.Fdeploy/clean.sh", $@"{AppState.Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh");
        //     
        //     if (AppState.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test copy... Failed!");
        //     else
        //         spinner.Text = "Test copy... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (AppState.CancellationTokenSource.IsCancellationRequested)
        //     return;

        // await Spinner.StartAsync("Test delete folder...", async spinner =>
        // {
        //     AppState.Spinner = spinner;
        //
        //     await Smb.DeleteServerFolderRecursiveAsync(AppState, $@"{AppState.Settings.Paths.RemoteRootPath}\wwwroot\xxxx");
        //     
        //     if (AppState.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test delete folder... Failed!");
        //     else
        //         spinner.Text = "Test delete folder... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (AppState.CancellationTokenSource.IsCancellationRequested)
        //     return;

        // await Spinner.StartAsync("Test delete...", async spinner =>
        // {
        //     AppState.Spinner = spinner;
        //
        //     await Smb.DeleteServerFileAsync(AppState, $@"{AppState.Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh");
        //     
        //     if (AppState.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test delete... Failed!");
        //     else
        //         spinner.Text = "Test delete... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (AppState.CancellationTokenSource.IsCancellationRequested)
        //     return;
        
        #endregion
        
        #region Publish Project

        AppState.PublishPath = $"{AppState.WorkingPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        AppState.TrimmablePublishPath = AppState.PublishPath.TrimPath();

        var sb = new StringBuilder();

        await Spinner.StartAsync($"Publishing project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{AppState.Settings.Project.TargetFramework:N1}", $"{AppState.WorkingPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}", "-c", AppState.Settings.Project.BuildConfiguration, "-o", AppState.PublishPath, $"/p:EnvironmentName={AppState.Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"Publishing project {AppState.Settings.Project.ProjectFileName}... Failed!");
                    AppState.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await AppState.CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"Published project {AppState.Settings.Project.ProjectFileName}... Success!";
            }

            catch (Exception e)
            {
                spinner.Fail($"Publishing project {AppState.Settings.Project.ProjectFileName}... Failed!");
                AppState.Exceptions.Add($"Could not publish the project; {e.Message}");
                await AppState.CancellationTokenSource.CancelAsync();
            }
            
        }, Patterns.Dots, Patterns.Line);

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            await Storage.RecurseLocalPathAsync(AppState, AppState.PublishPath);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing local files... Failed!");
            else        
                spinner.Text = $"Indexing local files... {AppState.LocalFiles.Count(f => f.IsFile):N0} files... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
       
        #region Index Server Files

        await Storage.EnsureFileStoreAsync(AppState);
            
        if (AppState.FileStore is null)
            return;
        
        await Spinner.StartAsync("Indexing server files...", async spinner =>
        {
            AppState.CurrentSpinner = spinner;
            
            await Storage.RecurseSmbPathAsync(AppState, AppState.Settings.Paths.RemoteRootPath.NormalizeSmbPath());

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing server files... Failed!");
            else
                spinner.Text = $"Indexing server files... {AppState.ServerFiles.Count(f => f.IsFile):N0} files... Success!";

        }, Patterns.Dots, Patterns.Line);

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion
        
        #region Process Deletions

        if (AppState.Settings.DeleteOrphans)
        {
            var itemsToDelete = AppState.ServerFiles.Except(AppState.LocalFiles, new FileObjectComparer()).ToList();

            
            
            





            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }
        
        #endregion
        
        #region Static File Sync
        
        // foreach (var path in Settings.Paths.StaticFilePaths)
        // {
        // }
        
        #endregion

        #region Process Static File Copies
        
        #endregion

        #region Take Server Offline
        
        #endregion
        
        #region File Sync
        
        #endregion
        
        #region Process File Copies
        
        #endregion
        
        #region Bring Server Online
        
        #endregion
        
        #region Local Cleanup
        
        #endregion
        
        Storage.Disconnect(AppState);
    }
    
    #endregion
}