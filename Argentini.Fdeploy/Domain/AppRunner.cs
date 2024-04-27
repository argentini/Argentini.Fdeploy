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

        SMB2Client? client = null;

        #region Publish Project

        AppState.PublishPath = $"{AppState.WorkingPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        AppState.TrimmablePublishPath = AppState.PublishPath.TrimPath();

        var sb = new StringBuilder();

        await Spinner.StartAsync($"Publishing project {AppState.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var spinnerText = spinner.Text;

                Timer.Restart();
                
                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{AppState.Settings.Project.TargetFramework:N1}", $"{AppState.WorkingPath}{Path.DirectorySeparatorChar}{AppState.Settings.Project.ProjectFilePath}", "-c", AppState.Settings.Project.BuildConfiguration, "-o", AppState.PublishPath, $"/p:EnvironmentName={AppState.Settings.Project.EnvironmentName}" })
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
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            var spinnerText = spinner.Text;

            Timer.Restart();
            
            await Storage.RecurseLocalPathAsync(AppState, AppState.PublishPath);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail($"{spinnerText} Failed!");
            else        
                spinner.Text = $"{spinnerText} {AppState.LocalFiles.Count(f => f.IsFile):N0} file{(AppState.LocalFiles.Count(f => f.IsFile) == 1 ? string.Empty : "s")}, {AppState.LocalFiles.Count(f => f.IsFolder):N0} folders ({Timer.Elapsed.FormatElapsedTime()})... Success!";
            
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

                Storage.RecurseServerPath(AppState, AppState.Settings.Paths.RemoteRootPath.NormalizeSmbPath());

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    spinner.Fail($"{spinnerText} Failed!");
                else
                    spinner.Text =
                        $"{spinnerText} {AppState.ServerFiles.Count(f => f.IsFile):N0} file{(AppState.ServerFiles.Count(f => f.IsFile) == 1 ? string.Empty : "s")}, {AppState.ServerFiles.Count(f => f.IsFolder):N0} folders ({Timer.Elapsed.FormatElapsedTime()})... Success!";

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

                    AppState.CurrentSpinner = spinner;
                    
                    Timer.Restart();

                    var itemsToDelete = AppState.ServerFiles.Except(AppState.LocalFiles, new FileObjectComparer()).Where(f => f.IsDeleted == false).ToList();

                    // Remove paths that enclose ignore paths
                    foreach (var item in itemsToDelete.ToList().Where(f => f.IsFolder).OrderBy(o => o.Level))
                    {
                        foreach (var ignorePath in AppState.Settings.Paths.RelativeIgnorePaths)
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
                            if (subitem.Level > item.Level &&
                                subitem.RelativeComparablePath.StartsWith(item.RelativeComparablePath))
                                itemsToDelete.Remove(subitem);
                        }
                    }

                    foreach (var item in itemsToDelete)
                    {
                        if (item.IsFile)
                            await Storage.DeleteServerFileAsync(AppState, fileStore, item);
                        else
                            await Storage.DeleteServerFolderRecursiveAsync(AppState, fileStore, item);

                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                            break;
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"Deleted orphan files and folders ({Timer.Elapsed.FormatElapsedTime()})... Success!";

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Copy Static File Paths

            if (AppState.Settings.Paths.StaticFilePaths.Any())
            {
                await Spinner.StartAsync("Copying static file paths...", async spinner =>
                {
                    var spinnerText = spinner.Text;
                    var foldersToCreate = new List<string>();

                    AppState.CurrentSpinner = spinner;

                    Timer.Restart();
                    
                    foreach (var folder in AppState.LocalFiles.ToList().Where(f => f is { IsFolder: true, IsStaticFilePath: true }))
                    {
                        if (AppState.ServerFiles.Any(f => f.IsFolder && f.RelativeComparablePath == folder.RelativeComparablePath) == false)
                            foldersToCreate.Add($"{AppState.Settings.Paths.RemoteRootPath.SetSmbPathSeparators().TrimPath()}\\{folder.RelativeComparablePath.SetSmbPathSeparators().TrimPath()}");
                    }

                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    foreach (var folder in foldersToCreate)
                    {
                        spinner.Text = $"{spinnerText} create: {folder}...";

                        Storage.EnsureServerPathExists(AppState, fileStore, folder);
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                        return;
                    }

                    var filesToCopy = AppState.LocalFiles.ToList().Where(f => f is { IsFile: true, IsStaticFilePath: true }).ToList();
                    var filesCopied = 0;

                    foreach (var file in filesToCopy)
                    {
                        var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == file.RelativeComparablePath && f.IsDeleted == false);

                        if (serverFile is not null && serverFile.LastWriteTime == file.LastWriteTime && serverFile.FileSizeBytes == file.FileSizeBytes)
                            continue;

                        spinner.Text = $"{spinnerText} {file.FileNameOrPathSegment}...";
                        await Storage.CopyFileAsync(AppState, fileStore, file);
                        filesCopied++;
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                    {
                        spinner.Fail($"{spinnerText} Failed!");
                    }
                    else
                    {
                        if (filesCopied != 0)
                            spinner.Text = $"{spinnerText} {filesToCopy.Count:N0} file{(filesToCopy.Count == 1 ? string.Empty : "s")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                        else
                            spinner.Text = $"{spinnerText} Nothing to copy... Success!";
                    }

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Process Static File Copies

            if (AppState.Settings.Paths.StaticFileCopies.Count != 0)
            {
                await Spinner.StartAsync("Copying static files...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    Timer.Restart();
                    
                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    var spinnerText = spinner.Text;

                    foreach (var file in AppState.Settings.Paths.StaticFileCopies)
                    {
                        await Storage.CopyFileAsync(AppState, fileStore, file.Source, file.Destination);
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} {AppState.Settings.Paths.StaticFileCopies.Count:N0} file{(AppState.Settings.Paths.StaticFileCopies.Count == 1 ? string.Empty : "s")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

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
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} Success!";

                }, Patterns.Dots, Patterns.Line);

                if (AppState.CancellationTokenSource.IsCancellationRequested)
                    return;
            }

            #endregion

            #region Deploy Files!

            Timer.Restart();
            
            var itemsToCopy = AppState.LocalFiles.Where(f => f.IsFile).ToList();

            foreach (var fo in itemsToCopy.ToList())
            {
                foreach (var path in AppState.Settings.Paths.StaticFilePaths)
                {
                    if (fo.RelativeComparablePath.StartsWith(path))
                        itemsToCopy.Remove(fo);
                }
            }

            var itemCount = itemsToCopy.Count;

            await Spinner.StartAsync("Deploy files...", async spinner =>
            {
                AppState.CurrentSpinner = spinner;

                var spinnerText = spinner.Text;
                var filesCopied = 0;

                if (itemCount > 0)
                {
                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    foreach (var fo in itemsToCopy)
                    {
                        var serverFile = AppState.ServerFiles.FirstOrDefault(f => f.RelativeComparablePath == fo.RelativeComparablePath && f.IsDeleted == false);

                        if (serverFile is not null && serverFile.LastWriteTime == fo.LastWriteTime && serverFile.FileSizeBytes == fo.FileSizeBytes)
                            continue;

                        await Storage.CopyFileAsync(AppState, fileStore, fo);

                        if (AppState.CancellationTokenSource.IsCancellationRequested)
                            break;

                        filesCopied++;
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} {filesCopied:N0} file{(filesCopied == 1 ? string.Empty : "s")} updated ({Timer.Elapsed.FormatElapsedTime()})... Success!";
                }
                else
                {
                    spinner.Text = $"{spinnerText} no files to update... Success!";
                }

            }, Patterns.Dots, Patterns.Line);

            if (AppState.CancellationTokenSource.IsCancellationRequested)
                return;

            #endregion

            #region Process File Copies

            if (AppState.Settings.Paths.FileCopies.Count != 0)
            {
                await Spinner.StartAsync("Processing file copies...", async spinner =>
                {
                    AppState.CurrentSpinner = spinner;

                    var spinnerText = spinner.Text;
                    var fileStore = Storage.GetFileStore(AppState, client);

                    if (fileStore is null || AppState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    Timer.Restart();
                    
                    foreach (var file in AppState.Settings.Paths.FileCopies)
                    {
                        await Storage.CopyFileAsync(AppState, fileStore, file.Source, file.Destination);
                    }

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} {AppState.Settings.Paths.FileCopies.Count:N0} file{(AppState.Settings.Paths.FileCopies.Count == 1 ? string.Empty : "s")} ({Timer.Elapsed.FormatElapsedTime()})... Success!";

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

                    await Storage.DeleteOfflineFileAsync(AppState, fileStore);

                    if (AppState.CancellationTokenSource.IsCancellationRequested)
                        spinner.Fail($"{spinnerText} Failed!");
                    else
                        spinner.Text = $"{spinnerText} Success!";

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