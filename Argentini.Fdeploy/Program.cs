namespace Argentini.Fdeploy;

internal class Program
{
	private static async Task Main(string[] args)
	{
		var cancellationTokenSource = new CancellationTokenSource();
		
		var totalTimer = new Stopwatch();

		totalTimer.Start();
		
		Console.OutputEncoding = Encoding.UTF8;
		
		var appState = new FdeployAppState(args, cancellationTokenSource);
		var version = await Identify.VersionAsync(System.Reflection.Assembly.GetExecutingAssembly());
        
		if (appState.VersionMode)
		{
			await Console.Out.WriteLineAsync($"Fdeploy Version {version}");
			Environment.Exit(0);
		}
		
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(FdeployAppState.MaxConsoleWidth));
		await Console.Out.WriteLineAsync("Fdeploy: Deploy .NET web applications using SMB on Linux, macOS, or Windows");
		await Console.Out.WriteLineAsync($"Version {version} for {Identify.GetOsPlatformName()} (.NET {Identify.GetRuntimeVersion()}/{Identify.GetProcessorArchitecture()})");
		
		await Console.Out.WriteLineAsync(Strings.ThickLine.Repeat(FdeployAppState.MaxConsoleWidth));
		
		if (appState.InitMode)
		{
			var yaml = await File.ReadAllTextAsync(Path.Combine(await appState.GetEmbeddedYamlPathAsync(cancellationTokenSource), "fdeploy.yml"), cancellationTokenSource.Token);

            if (cancellationTokenSource.IsCancellationRequested == false)
    			await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), "fdeploy.yml"), yaml, cancellationTokenSource.Token);			
			
            if (cancellationTokenSource.IsCancellationRequested == false)
            {
			    await Console.Out.WriteLineAsync($"Created fdeploy.yml file at {Directory.GetCurrentDirectory()}");
			    await Console.Out.WriteLineAsync();
			
    			Environment.Exit(0);
            }
		}
		
		else if (appState.HelpMode)
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

			Environment.Exit(0);
		}

		await FdeployAppState.ColonOut("Settings File", appState.YamlProjectFilePath);

        if (cancellationTokenSource.IsCancellationRequested)
        {
            await appState.OutputExceptionsAsync();

            Environment.Exit(1);
        }
        
        await FdeployAppState.ColonOut("Started Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await Console.Out.WriteLineAsync();



        
        
        
        
        
        
        
        await FdeployAppState.ColonOut("Completed Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await FdeployAppState.ColonOut("Total Run Time", $"{TimeSpan.FromMilliseconds(totalTimer.ElapsedMilliseconds):c}");

        await Console.Out.WriteLineAsync();
        
		Environment.Exit(0);
	}
}
