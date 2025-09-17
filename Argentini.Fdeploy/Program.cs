using Spectre.Console;

namespace Argentini.Fdeploy;

internal abstract class Program
{
	private static async Task Main(string[] args)
	{
        Console.OutputEncoding = Encoding.UTF8;

        var totalTimer = new Stopwatch();

        totalTimer.Start();
        
        var runner = new AppRunner(args);

        if (runner.AppState.CancellationTokenSource.IsCancellationRequested)
        {
            runner.OutputExceptions();
        }

        await runner.DeployAsync();
        
        if (runner.AppState.CancellationTokenSource.IsCancellationRequested)
        {
            runner.OutputExceptions();
            AnsiConsole.MarkupLine(string.Empty);
        }

        else
        {
            if (runner is { VersionMode: false, HelpMode: false, InitMode: false })
            {
                AnsiConsole.MarkupLine(string.Empty);
                AppRunner.ColonOut("Completed Deployment", $"{DateTime.Now:HH:mm:ss.fff}");
            }
        }
        
        if (runner is { VersionMode: false, HelpMode: false, InitMode: false })
            AppRunner.ColonOut("Total Run Time", $"{totalTimer.Elapsed.FormatElapsedTime()}");

        AnsiConsole.MarkupLine(string.Empty);

        Environment.Exit(runner.AppState.CancellationTokenSource.IsCancellationRequested ? 1 : 0);
    }
}
