namespace Argentini.Fdeploy;

internal class Program
{
	private static async Task Main(string[] args)
	{
        Console.OutputEncoding = Encoding.UTF8;

        var runner = new AppRunner(args);

        if (runner.CancellationTokenSource.IsCancellationRequested)
        {
            await runner.OutputExceptionsAsync();
        }

        await runner.DeployAsync();

        if (runner.CancellationTokenSource.IsCancellationRequested)
        {
            await runner.OutputExceptionsAsync();
            await Console.Out.WriteLineAsync();
        }

        else
        {
            if (runner is { VersionMode: false, HelpMode: false, InitMode: false })
            {
                await Console.Out.WriteLineAsync();
                await AppRunner.ColonOutAsync("Completed Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
            }
        }
        
        if (runner is { VersionMode: false, HelpMode: false, InitMode: false })
            await AppRunner.ColonOutAsync("Total Run Time", $"{TimeSpan.FromMilliseconds(runner.Timer.ElapsedMilliseconds):c}");

        await Console.Out.WriteLineAsync();

        if (runner.CancellationTokenSource.IsCancellationRequested)
            Environment.Exit(1);
        else
            Environment.Exit(0);
    }
}
