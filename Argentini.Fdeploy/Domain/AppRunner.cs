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

    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public List<string> Exceptions { get; } = [];
    public List<string> CliArguments { get; } = [];
    public Settings Settings { get; set; } = new();
    public string AppOfflineMarkup { get; set; } = string.Empty;
    public string YamlProjectFilePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;

    #endregion
    
    #region Properties
    
    public StorageRunner StorageRunner { get; } = null!;
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
        
        if (File.Exists(YamlProjectFilePath) == false)
        {
            Exceptions.Add($"Could not find project file `{YamlProjectFilePath}`");
            CancellationTokenSource.Cancel();
            return;
        }

        if (YamlProjectFilePath.IndexOf(Path.DirectorySeparatorChar) < 0)
        {
            Exceptions.Add($"Invalid project file path `{YamlProjectFilePath}`");
            CancellationTokenSource.Cancel();
            return;
        }
            
        WorkingPath = YamlProjectFilePath[..YamlProjectFilePath.LastIndexOf(Path.DirectorySeparatorChar)];
        
        var yaml = File.ReadAllText(YamlProjectFilePath);
        var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
        
        Settings = deserializer.Deserialize<Settings>(yaml);
        
        #endregion

        #region Normalize Paths

        Settings.Project.ProjectFilePath = Settings.Project.ProjectFilePath.NormalizePath();
        Settings.Paths.RemoteRootPath = Settings.Paths.RemoteRootPath.NormalizePath();

        Settings.Paths.StaticFilePaths.NormalizePaths();
        Settings.Paths.RelativeIgnorePaths.NormalizePaths();
        Settings.Paths.RelativeIgnoreFilePaths.NormalizePaths();

        foreach (var copy in Settings.Paths.StaticFileCopies)
        {
            copy.Source = copy.Source.NormalizePath();
            copy.Destination = copy.Destination.NormalizePath();
        }
        
        foreach (var copy in Settings.Paths.FileCopies)
        {
            copy.Source = copy.Source.NormalizePath();
            copy.Destination = copy.Destination.NormalizePath();
        }
        
        #endregion
        
        StorageRunner = new StorageRunner(Settings, Exceptions, CancellationTokenSource, WorkingPath);
    }

    public async ValueTask DeployAsync()
    {
		Timer.Start();
		
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
			var yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(CancellationTokenSource), "fdeploy.yml"), CancellationTokenSource.Token);

            if (CancellationTokenSource.IsCancellationRequested == false)
    			await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml"), yaml, CancellationTokenSource.Token);
			
            if (CancellationTokenSource.IsCancellationRequested == false)
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

        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        AppOfflineMarkup = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedHtmlPathAsync(CancellationTokenSource), "app_offline.htm"), CancellationTokenSource.Token);
        AppOfflineMarkup = AppOfflineMarkup.Replace("{{MetaTitle}}", Settings.Offline.MetaTitle);
        AppOfflineMarkup = AppOfflineMarkup.Replace("{{PageTitle}}", Settings.Offline.PageTitle);
        AppOfflineMarkup = AppOfflineMarkup.Replace("{{PageHtml}}", Settings.Offline.PageHtml);
        
        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();
        
        await StorageRunner.RunDeploymentAsync();
        
        StorageRunner.Disconnect();
    }
    
    public async ValueTask<string> GetEmbeddedYamlPathAsync(CancellationTokenSource cancellationToken)
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
            Exceptions.Add("Embedded YAML resources cannot be found.");
            await cancellationToken.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    public async ValueTask<string> GetEmbeddedHtmlPathAsync(CancellationTokenSource cancellationToken)
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
            Exceptions.Add("Embedded HTML resources cannot be found.");
            await cancellationToken.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    public async ValueTask OutputExceptionsAsync()
    {
        foreach (var message in Exceptions)
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
}