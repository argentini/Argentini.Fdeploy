namespace Argentini.Fdeploy;

internal class Program
{
	private static async Task Main(string[] args)
	{
        Console.OutputEncoding = Encoding.UTF8;

        var runner = new AppRunner(args);

        if (runner.AppState.CancellationTokenSource.IsCancellationRequested)
        {
            await runner.OutputExceptionsAsync();
        }

        await runner.DeployAsync();
        
        if (runner.AppState.CancellationTokenSource.IsCancellationRequested)
        {
            await runner.OutputExceptionsAsync();
        }
        
        await AppRunner.ColonOutAsync("Completed Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
        await AppRunner.ColonOutAsync("Total Run Time", $"{TimeSpan.FromMilliseconds(runner.Timer.ElapsedMilliseconds):c}");

        await Console.Out.WriteLineAsync();
	}
}
