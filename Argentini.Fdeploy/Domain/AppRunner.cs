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
    public string AppOfflineMarkup { get; set; } = string.Empty;
    public string YamlProjectFilePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;
    public string PublishPath { get; set; } = string.Empty;
    public string TrimmablePublishPath { get; set; } = string.Empty;

    public SmbConfig SmbConfig { get; } = new();

    #endregion
    
    #region Properties
    
    public Stopwatch Timer { get; } = new();

    #endregion
    
    public AppRunner(IEnumerable<string> args)
    {
        #region Process Arguments
        
        CliArguments.AddRange(args);

        if (CliArguments.Count == 0)
            YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml");
        
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
                YamlProjectFilePath = projectFilePath;
            }
            else
            {
                if (projectFilePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                    YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), projectFilePath);
                else
                    YamlProjectFilePath = Path.Combine(Directory.GetCurrentDirectory(), $"fdeploy-{projectFilePath}.yml");
            }
        }
        
        #if DEBUG

        YamlProjectFilePath = Path.Combine("/Users/magic/Developer/Fynydd-Website-2024/UmbracoCms", "fdeploy-staging.yml");
        
        #endif

        #endregion
        
        #region Load Settings
    
        SmbConfig = new SmbConfig
        {
            Client = new SMB2Client()
        };

        if (File.Exists(YamlProjectFilePath) == false)
        {
            SmbConfig.Exceptions.Add($"Could not find project file `{YamlProjectFilePath}`");
            SmbConfig.CancellationTokenSource.Cancel();
            return;
        }

        if (YamlProjectFilePath.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            SmbConfig.Exceptions.Add($"Invalid project file path `{YamlProjectFilePath}`");
            SmbConfig.CancellationTokenSource.Cancel();
            return;
        }
            
        WorkingPath = YamlProjectFilePath[..YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        SmbConfig.Settings = deserializer.Deserialize<Settings>(yaml);
        
        #endregion

        #region Normalize Paths

        SmbConfig.Settings.Project.ProjectFilePath = SmbConfig.Settings.Project.ProjectFilePath.NormalizePath();
        SmbConfig.Settings.Paths.RemoteRootPath = SmbConfig.Settings.Paths.RemoteRootPath.NormalizePath();

        SmbConfig.Settings.Paths.StaticFilePaths.NormalizePaths();
        SmbConfig.Settings.Paths.RelativeIgnorePaths.NormalizePaths();
        SmbConfig.Settings.Paths.RelativeIgnoreFilePaths.NormalizePaths();

        foreach (var copy in SmbConfig.Settings.Paths.StaticFileCopies)
        {
            copy.Source = copy.Source.NormalizePath();
            copy.Destination = copy.Destination.NormalizePath();
        }
        
        foreach (var copy in SmbConfig.Settings.Paths.FileCopies)
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
            SmbConfig.Exceptions.Add("Embedded YAML resources cannot be found.");
            await SmbConfig.CancellationTokenSource.CancelAsync();
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
            SmbConfig.Exceptions.Add("Embedded HTML resources cannot be found.");
            await SmbConfig.CancellationTokenSource.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    #endregion
    
    #region Console Output
    
    public async ValueTask OutputExceptionsAsync()
    {
        foreach (var message in SmbConfig.Exceptions)
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
    
    #region Storage
   
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
			var yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(), "fdeploy.yml"), SmbConfig.CancellationTokenSource.Token);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested == false)
    			await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml"), yaml, SmbConfig.CancellationTokenSource.Token);
			
            if (SmbConfig.CancellationTokenSource.IsCancellationRequested == false)
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

		await ColonOutAsync("Settings File", YamlProjectFilePath);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion

        AppOfflineMarkup = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedHtmlPathAsync(), "AppOffline.html"), SmbConfig.CancellationTokenSource.Token);
        AppOfflineMarkup = AppOfflineMarkup.Replace("{{MetaTitle}}", SmbConfig.Settings.Offline.MetaTitle);
        AppOfflineMarkup = AppOfflineMarkup.Replace("{{PageTitle}}", SmbConfig.Settings.Offline.PageTitle);
        AppOfflineMarkup = AppOfflineMarkup.Replace("{{PageHtml}}", SmbConfig.Settings.Offline.PageHtml);

        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();
        
        #region Connect to Server

        await Spinner.StartAsync($"Connecting to {SmbConfig.Settings.ServerConnection.ServerAddress}...", spinner =>
        {
            Smb.Connect(SmbConfig);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            {
                spinner.Fail($"Connecting to {SmbConfig.Settings.ServerConnection.ServerAddress}... Failed!");
                Smb.Disconnect(SmbConfig);
            
                return Task.CompletedTask;
            }

            spinner.Text = $"Connecting to {SmbConfig.Settings.ServerConnection.ServerAddress}... Success!";
            
            return Task.CompletedTask;
            
        }, Patterns.Dots, Patterns.Line);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        #region Test Code
        
        // await Spinner.StartAsync("Test copy...", async spinner =>
        // {
        //     SmbConfig.Spinner = spinner;
        //     
        //     await Smb.CopyFileAsync(SmbConfig, "/Users/magic/Developer/Argentini.Fdeploy/clean.sh", $@"{SmbConfig.Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh");
        //     
        //     if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test copy... Failed!");
        //     else
        //         spinner.Text = "Test copy... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //     return;

        // await Spinner.StartAsync("Test delete folder...", async spinner =>
        // {
        //     SmbConfig.Spinner = spinner;
        //
        //     await Smb.DeleteServerFolderRecursiveAsync(SmbConfig, $@"{SmbConfig.Settings.Paths.RemoteRootPath}\wwwroot\xxxx");
        //     
        //     if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test delete folder... Failed!");
        //     else
        //         spinner.Text = "Test delete folder... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //     return;

        // await Spinner.StartAsync("Test delete...", async spinner =>
        // {
        //     SmbConfig.Spinner = spinner;
        //
        //     await Smb.DeleteServerFileAsync(SmbConfig, $@"{SmbConfig.Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh");
        //     
        //     if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test delete... Failed!");
        //     else
        //         spinner.Text = "Test delete... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //     return;
        
        #endregion
        
        #region Publish Project

        PublishPath = $"{WorkingPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        TrimmablePublishPath = PublishPath.TrimPath();

        var sb = new StringBuilder();

        await Spinner.StartAsync($"Publishing project {SmbConfig.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{SmbConfig.Settings.Project.TargetFramework:N1}", $"{WorkingPath}{Path.DirectorySeparatorChar}{SmbConfig.Settings.Project.ProjectFilePath}", "-c", SmbConfig.Settings.Project.BuildConfiguration, "-o", PublishPath, $"/p:EnvironmentName={SmbConfig.Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"Publishing project {SmbConfig.Settings.Project.ProjectFileName}... Failed!");
                    SmbConfig.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await SmbConfig.CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"Published project {SmbConfig.Settings.Project.ProjectFileName}... Success!";
            }

            catch (Exception e)
            {
                spinner.Fail($"Publishing project {SmbConfig.Settings.Project.ProjectFileName}... Failed!");
                SmbConfig.Exceptions.Add($"Could not publish the project; {e.Message}");
                await SmbConfig.CancellationTokenSource.CancelAsync();
            }
            
        }, Patterns.Dots, Patterns.Line);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            await RecurseLocalPathAsync(PublishPath);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing local files... Failed!");
            else        
                spinner.Text = $"Indexing local files... {SmbConfig.LocalFiles.Count(f => f.IsFile):N0} files... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
       
        #region Index Server Files

        await Smb.EnsureFileStoreAsync(SmbConfig);
            
        if (SmbConfig.FileStore is null)
            return;
        
        await Spinner.StartAsync("Indexing server files...", async spinner =>
        {
            SmbConfig.Spinner = spinner;
            
            await Smb.RecurseSmbPathAsync(SmbConfig, SmbConfig.Settings.Paths.RemoteRootPath.NormalizeSmbPath(), 0);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing server files... Failed!");
            else
                spinner.Text = $"Indexing server files... {SmbConfig.ServerFiles.Count(f => f.IsFile):N0} files... Success!";

        }, Patterns.Dots, Patterns.Line);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion
        
        #region Process Deletions

        if (SmbConfig.Settings.DeleteOrphans)
        {
            var filesToDelete = new List<FileObject>();

            
            
            





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
        
        Smb.Disconnect(SmbConfig);
    }

    private async ValueTask RecurseLocalPathAsync(string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                continue;
            
            var trimmed = subdir.TrimPath().TrimStart(TrimmablePublishPath).TrimPath(); 
            
            if (SmbConfig.Settings.Paths.RelativeIgnorePaths.Contains(trimmed) || SmbConfig.Settings.Paths.IgnoreFoldersNamed.Contains(subdir.GetLastPathSegment()))
                continue;

            SmbConfig.LocalFiles.Add(new FileObject
            {
                FullPath = subdir,
                FilePath = subdir.TrimPath().TrimEnd(subdir.GetLastPathSegment()).TrimPath(),
                FileName = subdir.GetLastPathSegment().TrimPath(),
                LastWriteTime = directory.LastWriteTime.ToFileTimeUtc(),
                FileSizeBytes = 0,
                IsFolder = true
            });

            await RecurseLocalPathAsync(subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path))
        {
            try
            {
                var file = new FileInfo(filePath);

                if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    continue;
                
                var trimmed = filePath.TrimPath().TrimStart(TrimmablePublishPath).TrimPath(); 

                if (SmbConfig.Settings.Paths.RelativeIgnoreFilePaths.Contains(trimmed) || SmbConfig.Settings.Paths.IgnoreFilesNamed.Contains(file.Name))
                    continue;

                SmbConfig.LocalFiles.Add(new FileObject
                {
                    FullPath = filePath,
                    FilePath = file.DirectoryName.TrimPath(),
                    FileName = file.Name,
                    LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                    FileSizeBytes = file.Length,
                    IsFile = true
                });
            }
            catch
            {
                SmbConfig.Exceptions.Add($"Could process local file `{filePath}`");
                await SmbConfig.CancellationTokenSource.CancelAsync();
            }
        }
    }

    #endregion
}