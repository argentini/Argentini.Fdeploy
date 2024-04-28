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
            
        AppState.ProjectPath = AppState.YamlProjectFilePath[..AppState.YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(AppState.YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        AppState.Settings = deserializer.Deserialize<Settings>(yaml);

        if (AppState.Settings.MaxThreadCount < 1)
            AppState.Settings.MaxThreadCount = new Settings().MaxThreadCount;
        
        #endregion

        #region Normalize Paths

        AppState.PublishPath = $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        AppState.TrimmablePublishPath = AppState.PublishPath.TrimPath();

        AppState.Settings.Project.ProjectFilePath = AppState.Settings.Project.ProjectFilePath.NormalizePath();
        AppState.Settings.ServerConnection.RemoteRootPath = AppState.Settings.ServerConnection.RemoteRootPath.NormalizeSmbPath();

        AppState.Settings.Project.CopyFilesToPublishFolder.NormalizePaths();
        AppState.Settings.Project.CopyFoldersToPublishFolder.NormalizePaths();

        AppState.Settings.Deployment.OnlineCopyFilePaths.NormalizePaths();
        AppState.Settings.Deployment.OnlineCopyFolderPaths.NormalizePaths();

        AppState.Settings.Deployment.IgnoreFilePaths.NormalizePaths();
        AppState.Settings.Deployment.IgnoreFolderPaths.NormalizePaths();

        var newList = new List<string>();
        
        foreach (var item in AppState.Settings.Project.CopyFilesToPublishFolder)
            newList.Add(item.NormalizePath().TrimStart(AppState.ProjectPath).TrimPath());

        AppState.Settings.Project.CopyFilesToPublishFolder.Clear();
        AppState.Settings.Project.CopyFilesToPublishFolder.AddRange(newList);

        newList.Clear();
        
        foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
            newList.Add(item.NormalizePath().TrimStart(AppState.ProjectPath).TrimPath());

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
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{MetaTitle}}", AppState.Settings.AppOffline.MetaTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageTitle}}", AppState.Settings.AppOffline.PageTitle);
        AppState.AppOfflineMarkup = AppState.AppOfflineMarkup.Replace("{{PageHtml}}", AppState.Settings.AppOffline.ContentHtml);

        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();

        SMB2Client? client = null;
        
        #region Local Cleanup

        if (Directory.Exists(AppState.PublishPath))
        {
            await Spinner.StartAsync("Delete existing publish folder...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;

                Directory.Delete(AppState.PublishPath, true);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinnerText} Failed!");
                else
                    spinner.Text = $"{spinnerText} Success!";

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);
        }

        #endregion
        
        #region Publish Project

        var sb = new StringBuilder();

        await Spinner.StartAsync($"Publishing project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var spinnerText = spinner.Text;

                Timer.Restart();
                
                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{AppState.Settings.Project.TargetFramework:N1}", $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}", "-c", AppState.Settings.Project.BuildConfiguration, "-o", AppState.PublishPath, $"/p:EnvironmentName={AppState.Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                    AppState.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await AppState.CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"Published project ({Timer.Elapsed.FormatElapsedTime()})... Success!";
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

        //await Storage.CopyFolderAsync(AppState, Path.Combine(AppState.WorkingPath, "wwwroot", "media"), Path.Combine(AppState.PublishPath, "wwwroot", "media"));
        
        #endregion
        
        #region Copy Additional Files Into Publish Folder

        if (AppState.Settings.Project.CopyFilesToPublishFolder.Count != 0)
        {
            await Spinner.StartAsync("Adding files to publish folder...", async spinner =>
            {
                var spinnerText = spinner.Text;

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
                        
                        spinner.Text = $"{spinnerText} {item.GetLastPathSegment()}...";

                        await Task.Delay(5);
                    }

                    catch
                    {
                        spinner.Fail($"{spinnerText} {item.GetLastPathSegment()}... Failed!");
                        AppState.Exceptions.Add($"Could not add file `{sourceFilePath} => {destFilePath}`");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Text = $"{spinnerText} {AppState.Settings.Project.CopyFilesToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFilesToPublishFolder.Count.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

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
                var spinnerText = spinner.Text;

                Timer.Restart();
                
                foreach (var item in AppState.Settings.Project.CopyFoldersToPublishFolder)
                {
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        break;
                    
                    try
                    {
                        Timer.Restart();

                        Storage.CopyFolder(AppState, $"{AppState.ProjectPath}{Path.DirectorySeparatorChar}{item}", $"{AppState.PublishPath}{Path.DirectorySeparatorChar}{item}");

                        spinner.Text = $"{spinnerText} {item.GetLastPathSegment()}...";
                        await Task.Delay(5);
                    }

                    catch
                    {
                        spinner.Fail($"{spinnerText} {item.GetLastPathSegment()}... Failed!");
                        AppState.Exceptions.Add($"Could not add folder `{item}`");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested == false)
                    spinner.Text = $"{spinnerText} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count:N0} {AppState.Settings.Project.CopyFoldersToPublishFolder.Count.Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;
        }
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            var spinnerText = spinner.Text;

            Timer.Restart();
            
            await Storage.RecurseLocalPathAsync(AppState, AppState.PublishPath);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail($"{spinnerText} Failed!");
            else        
                spinner.Text = $"{spinnerText} {AppState.LocalFiles.Count(f => f.IsFile):N0} {AppState.LocalFiles.Count(f => f.IsFile).Pluralize("file", "files")}, {AppState.LocalFiles.Count(f => f.IsFolder):N0} {AppState.LocalFiles.Count(f => f.IsFolder).Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        try
        {
            #region Verify Server Connection And Share

            client = Storage.ConnectClient(AppState, true);

            if (client is null || AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Index Server Files

            await Spinner.StartAsync("Indexing server files...", async spinner =>
            {
                var spinnerText = spinner.Text;

                AppState.CurrentSpinner = spinner;

                Timer.Restart();

                Storage.RecurseServerPath(AppState, AppState.Settings.ServerConnection.RemoteRootPath);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinnerText} Failed!");
                else
                    spinner.Text =
                        $"{spinnerText} {AppState.ServerFiles.Count(f => f.IsFile):N0} {AppState.ServerFiles.Count(f => f.IsFile).Pluralize("file", "files")}, {AppState.ServerFiles.Count(f => f.IsFolder):N0} {AppState.ServerFiles.Count(f => f.IsFolder).Pluralize("folder", "folders")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Deploy Online Copy Folders

            await Spinner.StartAsync("Deploying while online: folders...", async spinner =>
            {
                var spinnerText = spinner.Text;
                var foldersToCreate = new List<string>();
                var filesCopied = 0;

                AppState.CurrentSpinner = spinner;
                Timer.Restart();
                
                if (AppState.Settings.Deployment.OnlineCopyFolderPaths.Count > 0)
                {
                    foreach (var folder in AppState.LocalFiles.ToList().Where(f => f is { IsFolder: true, IsSafeCopy: true }))
                    {
                        if (AppState.ServerFiles.Any(f => f.IsFolder && f.RelativeComparablePath == folder.RelativeComparablePath) == false)
                            foldersToCreate.Add($"{AppState.Settings.ServerConnection.RemoteRootPath}\\{folder.RelativeComparablePath.SetSmbPathSeparators().TrimPath()}");
                    }

                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    foreach (var folder in foldersToCreate)
                    {
                        Storage.EnsureServerPathExists(AppState, fileStore, folder);
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                        return;
                    }

                    var filesToCopy = AppState.LocalFiles.ToList().Where(f => f is { IsFile: true, IsSafeCopy: true }).ToList();

                    Parallel.For(0, filesToCopy.Count, new ParallelOptions { MaxDegreeOfParallelism = AppState.Settings.MaxThreadCount }, (i, state) =>
                    {
                        if (AppState.CancellationTokenSource.IsCancellationRequested || state.ShouldExitCurrentIteration || state.IsStopped)
                            return;

                        var file = filesToCopy[i];
                        var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == file.RelativeComparablePath && f.IsDeleted == false);

                        if (serverFile is not null && serverFile.LastWriteTime == file.LastWriteTime && serverFile.FileSizeBytes == file.FileSizeBytes)
                            return;

                        spinner.Text = $"{spinnerText} {file.FileNameOrPathSegment}...";
                        Storage.CopyFile(AppState, fileStore, file);
                        filesCopied++;
                    });
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                }
                else
                {
                    if (filesCopied != 0)
                        spinner.Text = $"{spinnerText} {AppState.Settings.Deployment.OnlineCopyFolderPaths.Count:N0} {AppState.Settings.Deployment.OnlineCopyFolderPaths.Count.Pluralize("folder", "folders")} with {filesCopied:N0} {filesCopied.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                    else
                        spinner.Text = $"{spinnerText} Nothing to copy... Success!";
                }

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Deploy Online Copy Files

            await Spinner.StartAsync("Deploying while online: files...", async spinner =>
            {
                var spinnerText = spinner.Text;
                var filesCopied = 0;

                AppState.CurrentSpinner = spinner;
                Timer.Restart();

                if (AppState.Settings.Deployment.OnlineCopyFilePaths.Count > 0)
                {
                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    Parallel.For(0, AppState.Settings.Deployment.OnlineCopyFilePaths.Count, new ParallelOptions { MaxDegreeOfParallelism = AppState.Settings.MaxThreadCount }, (i, state) =>
                    {
                        if (AppState.CancellationTokenSource.IsCancellationRequested || state.ShouldExitCurrentIteration || state.IsStopped)
                            return;

                        var item = AppState.Settings.Deployment.OnlineCopyFilePaths[i];
                        var localFile = AppState.LocalFiles.FirstOrDefault(f => f.RelativeComparablePath == item && f.IsDeleted == false);

                        if (localFile is null)
                        {
                            spinner.Fail($"{spinnerText} {item.GetLastPathSegment()}... Failed!");
                            AppState.Exceptions.Add($"Local file does not exist: `{item}`");
                            AppState.CancellationTokenSource.Cancel();
                            return;
                        }
                        
                        var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == localFile.RelativeComparablePath && f.IsDeleted == false);

                        if (serverFile is not null && serverFile.LastWriteTime == localFile.LastWriteTime && serverFile.FileSizeBytes == localFile.FileSizeBytes)
                            return;
                        
                        Storage.CopyFile(AppState, fileStore, localFile);
                        filesCopied++;
                    });
                }

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                {
                    spinner.Fail($"{spinnerText} Failed!");
                }
                else
                {
                    if (filesCopied != 0)
                        spinner.Text = $"{spinnerText} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                    else
                        spinner.Text = $"{spinnerText} Nothing to copy... Success!";
                }

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Take Server Offline

            var offlineTimer = new Stopwatch();

            if (AppState.Settings.TakeServerOffline)
            {
                var fileStore = Storage.GetFileStore(AppState, client);

                if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                    return;

                offlineTimer.Start();

                await Spinner.StartAsync("Take website offline...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    var spinnerText = spinner.Text;

                    await Storage.CreateOfflineFileAsync(AppState, fileStore);

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                    }
                    else
                    {
                        if (AppState.Settings.ServerOfflineDelaySeconds > 0)
                        {
                            for (var i = AppState.Settings.ServerOfflineDelaySeconds; i >= 0; i--)
                            {
                                spinner.Text = $"{spinnerText} Done... Waiting ({i:N0})";

                                await Task.Delay(1000);
                            }
                        }

                        spinner.Text = $"{spinnerText} Success!";
                    }

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Deploy Offline Files

            Timer.Restart();
            
            var itemsToCopy = AppState.LocalFiles.Where(f => f is { IsFile: true, IsSafeCopy: false }).ToList();
            var itemCount = itemsToCopy.Count;

            await Spinner.StartAsync("Deploy files...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var filesCopied = 0;

                if (itemCount > 0)
                {
                    Parallel.For(0, itemsToCopy.Count, new ParallelOptions { MaxDegreeOfParallelism = AppState.Settings.MaxThreadCount }, (i, state) =>
                    {
                        if (AppState.CancellationTokenSource.IsCancellationRequested || state.ShouldExitCurrentIteration || state.IsStopped)
                            return;

                        SMB2Client? innerClient = null;

                        try
                        {
                            var fo = itemsToCopy[i];
                            var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == fo.RelativeComparablePath && f.IsDeleted == false);

                            if (serverFile is not null && serverFile.LastWriteTime == fo.LastWriteTime && serverFile.FileSizeBytes == fo.FileSizeBytes)
                                return;

                            innerClient = Storage.ConnectClient(AppState);
                        
                            if (innerClient is null || AppState.CancellationTokenSource.IsCancellationRequested)
                                return;

                            var fileStore = Storage.GetFileStore(AppState, innerClient);

                            if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                                return;
                            
                            Storage.CopyFile(AppState, fileStore, fo);

                            if (AppState.CancellationTokenSource.IsCancellationRequested)
                            {
                                state.Stop();
                                return;
                            }

                            filesCopied++;
                        }
                        finally
                        {
                            Storage.DisconnectClient(innerClient);
                        }
                    });

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} {filesCopied:N0} {filesCopied.Pluralize("file", "files")} updated ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                }
                else
                {
                    spinner.Text = $"{spinnerText} no files to update... Success!";
                }

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Process Deletions

            if (AppState.Settings.DeleteOrphans)
            {
                var fileStore = Storage.GetFileStore(AppState, client);

                if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                    return;

                await Spinner.StartAsync("Deleting orphaned files and folders...", async spinner =>
                {
                    var spinnerText = spinner.Text;
                    var filesRemoved = 0;
                    var foldersRemoved = 0;

                    AppState.CurrentSpinner = spinner;
                    
                    Timer.Restart();

                    var itemsToDelete = AppState.ServerFiles.Except(AppState.LocalFiles, new FileObjectComparer()).Where(f => f.IsDeleted == false).ToList();

                    // Remove paths that enclose ignore paths
                    foreach (var item in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                    {
                        foreach (var ignorePath in AppState.Settings.Deployment.IgnoreFolderPaths)
                        {
                            if (ignorePath.StartsWith(item.RelativeComparablePath) == false)
                                continue;

                            itemsToDelete.Remove(item);
                        }
                    }

                    // Remove descendants of folders to be deleted
                    foreach (var item in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                    {
                        foreach (var subitem in itemsToDelete.ToList().OrderByDescending(o => o.Level))
                        {
                            if (subitem.Level > item.Level && subitem.RelativeComparablePath.StartsWith(item.RelativeComparablePath))
                                itemsToDelete.Remove(subitem);
                        }
                    }

                    foreach (var item in itemsToDelete)
                    {
                        if (item.IsFile)
                        {
                            await Storage.DeleteServerFileAsync(AppState, fileStore, item);
                            filesRemoved++;
                        }
                        else
                        {
                            await Storage.DeleteServerFolderRecursiveAsync(AppState, fileStore, item);
                            foldersRemoved++;
                        }

                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                            break;
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"Deleted {filesRemoved:N0} orphaned {filesRemoved.Pluralize("file", "files")} and {foldersRemoved:N0} {foldersRemoved.Pluralize("folder", "folders")} of orphaned files ({Timer.Elapsed.FormatElapsedTime()})... Success!";

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

                    var spinnerText = spinner.Text;
                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    if (AppState.Settings.ServerOnlineDelaySeconds > 0)
                    {
                        for (var i = AppState.Settings.ServerOnlineDelaySeconds; i >= 0; i--)
                        {
                            spinner.Text = $"{spinnerText}... Waiting ({i:N0})";
                            await Task.Delay(1000);
                        }
                    }

                    await Storage.DeleteOfflineFileAsync(AppState, fileStore);
                    
                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                    }
                    else
                    {
                        spinner.Text = $"{spinnerText} Success!";
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
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;

                Directory.Delete(AppState.PublishPath, true);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinnerText} Failed!");
                else
                    spinner.Text = $"{spinnerText} Success!";

                await Task.CompletedTask;

            }, Patterns.Dots, Patterns.Line);

            #endregion
        }
        finally
        {
            Storage.DisconnectClient(client);
        }
    }
    
    #endregion
}