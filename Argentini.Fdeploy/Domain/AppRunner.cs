namespace Argentini.Fdeploy.Domain;

public class AppRunner
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

    public static string CliErrorPrefix => "Fdeploy => ";

    #endregion

    #region Properties
    
    public FdeployAppState AppState { get; set; }
    public StorageService StorageService { get; set; }
    public Stopwatch Timer { get; set; } = new();

    #endregion
    
    public AppRunner(IEnumerable<string> args)
    {
        AppState = new FdeployAppState(args);
        StorageService = new StorageService(AppState);
    }

    public async ValueTask DeployAsync()
    {
		Timer.Start();
		
		var version = await Identify.VersionAsync(System.Reflection.Assembly.GetExecutingAssembly());
        
		if (AppState.VersionMode)
		{
			await Console.Out.WriteLineAsync($"Fdeploy Version {version}");
			return;
		}
		
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(MaxConsoleWidth));
		await Console.Out.WriteLineAsync("Fdeploy: Deploy .NET web applications using SMB on Linux, macOS, or Windows");
		await Console.Out.WriteLineAsync($"Version {version} for {Identify.GetOsPlatformName()} (.NET {Identify.GetRuntimeVersion()}/{Identify.GetProcessorArchitecture()})");
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(MaxConsoleWidth));
		
		if (AppState.InitMode)
		{
			var yaml = await File.ReadAllTextAsync(Path.Combine(await GetEmbeddedYamlPathAsync(AppState.CancellationTokenSource), "fdeploy.yml"), AppState.CancellationTokenSource.Token);

            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
    			await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml"), yaml, AppState.CancellationTokenSource.Token);
			
            if (AppState.CancellationTokenSource.IsCancellationRequested == false)
            {
			    await Console.Out.WriteLineAsync($"Created fdeploy.yml file at {Directory.GetCurrentDirectory()}");
			    await Console.Out.WriteLineAsync();
    			return;
            }
		}
		
		else if (AppState.HelpMode)
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
        
        await ColonOutAsync("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();

        await StorageService.ConnectAsync();        

        if (AppState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        
        
        
        
        
        
        
        
        StorageService.Disconnect();
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
            AppState.Exceptions.Add("Embedded YAML resources cannot be found.");
            await cancellationToken.CancelAsync();
            return string.Empty;
        }
        
        return workingPath;
    }

    public async ValueTask OutputExceptionsAsync()
    {
        await Console.Out.WriteLineAsync();

        foreach (var message in AppState.Exceptions)
            await Console.Out.WriteLineAsync($"{CliErrorPrefix}{message}");
        
        await Console.Out.WriteLineAsync();
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