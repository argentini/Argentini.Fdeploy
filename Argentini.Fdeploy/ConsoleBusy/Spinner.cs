namespace Argentini.Fdeploy.ConsoleBusy
{
    public class Spinner(
        string text,
        Pattern? pattern = null,
        ConsoleColor? color = null,
        bool enabled = true,
        Pattern? fallbackPattern = null)
        : IDisposable
    {
        private static readonly object SConsoleLock;
        private static readonly LinkedList<Spinner> SRunningSpinners = [];

        static Spinner()
        {
            // try to get internal Console's lock object( .NET 6 )
            var lockObject = typeof(Console).GetField("s_syncObject", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            
            if (lockObject is not null)
            {
                SConsoleLock = lockObject;
            }
            else
            {
                SConsoleLock = new object();
            }
        }

        private readonly CancellationTokenSource _cancellationTokenSource = new();
        private readonly bool _enabled = enabled && ConsoleHelper.IsRunningOnCi == false && Console.IsOutputRedirected == false;
        private Task? _task;
        private Pattern _pattern = pattern ?? DefaultPattern;
        private Pattern _fallbackPattern = fallbackPattern ?? DefaultPattern;
        private int _frameIndex;
        private int _lineLength;
        private int _cursorTop;
        private LinkedListNode<Spinner>? _nodeSelf;

        public bool Stopped { get; private set; }
        public SymbolDefinition SymbolSucceed { get; set; } = new SymbolDefinition("●", "•");
        public SymbolDefinition SymbolFailed { get; set; } = new SymbolDefinition("●", "•");
        public SymbolDefinition SymbolWarn { get; set; } = new SymbolDefinition("⚠", "[!]");
        public SymbolDefinition SymbolInfo { get; set; } = new SymbolDefinition("ℹ", "[i]");

        public ConsoleColor? Color { get; set; } = color;
        public string Text { get; set; } = text;
        public string OriginalText { get; set; } = text;
        public string RootText => Text.IndexOf("...", StringComparison.Ordinal) > 0 ? Text[..Text.IndexOf("...", StringComparison.Ordinal)] : Text; 

        private static Pattern DefaultPattern =>
            ConsoleHelper.ShouldFallback
                ? Patterns.Line
                : Patterns.Dots;

        private Pattern CurrentPattern =>
            ConsoleHelper.ShouldFallback
                ? _fallbackPattern
                : _pattern;

        public void Start()
        {
            Start(Environment.NewLine);
        }

        public void Start(string terminator)
        {
            if (_enabled == false) return;
            if (_task is not null) throw new InvalidOperationException("Spinner is already running");

            Stopped = false;
            lock (SConsoleLock)
            {
                lock (Console.Out)
                {
                    if (SRunningSpinners.Count == 0)
                    {
                        ConsoleHelper.TryEnableEscapeSequence();
                        ConsoleHelper.SetCursorVisibility(false);
                    }

                    _cursorTop = Console.CursorTop;
                    Console.Write(terminator);
                    Console.Out.Flush();

                    if (_cursorTop == Console.CursorTop)
                    {
                        _cursorTop = Math.Max(_cursorTop - 1, 0);
                        foreach (var item in SRunningSpinners)
                        {
                            item._cursorTop = Math.Max(item._cursorTop - 1, 0);
                        }
                    }

                    _nodeSelf = SRunningSpinners.AddLast(this);
                }
            }

            _task = Task.Run(async () =>
            {
                _frameIndex = 0;
                while (_cancellationTokenSource.IsCancellationRequested == false)
                {
                    Render(terminator);
                    await Task.Delay(CurrentPattern.Interval).ConfigureAwait(false);
                }
            });
        }

        private void Render(string terminator)
        {
            var pattern = CurrentPattern;
            var frame = pattern.Frames[_frameIndex++ % pattern.Frames.Length];

            lock (SConsoleLock)
            {
                lock (Console.Out)
                {
                    if (_enabled)
                    {
                        var currentLeft = Console.CursorLeft;
                        var currentTop = Console.CursorTop;

                        ConsoleHelper.ClearCurrentConsoleLine(_lineLength, _enabled ? _cursorTop : currentTop);
                        ConsoleHelper.WriteWithColor(frame, Color ?? Console.ForegroundColor);
                        Console.Write(" ");
                        Console.Write(Text);
                        _lineLength = Console.CursorLeft;
                        Console.Write(terminator);
                        Console.Out.Flush();

                        Console.SetCursorPosition(currentLeft, currentTop);
                    }
                    else
                    {

                        ConsoleHelper.WriteWithColor(frame, Color ?? Console.ForegroundColor);
                        Console.Write(" ");
                        Console.Write(Text);
                        _lineLength = frame.Length + 1 + Text.Length;
                        Console.Write(terminator);
                        Console.Out.Flush();
                    }
                }
            }
        }

        public void Dispose()
        {
            if (_cancellationTokenSource.IsCancellationRequested == false)
            {
                Stop(symbol: null, color: null);
            }
        }

        public void Stop(string? text = null, string? symbol = null, ConsoleColor? color = null)
        {
            Stop(text, symbol, color, Environment.NewLine);
        }

        public void Stop(string? text, string? symbol, ConsoleColor? color, string terminator)
        {
            if (_cancellationTokenSource.IsCancellationRequested) return;

            _cancellationTokenSource.Cancel();
            _task?.Wait();

            Color = color ?? Color;
            Text = text ?? Text;
            Stopped = true;

            _pattern = _fallbackPattern = new Pattern(new[] { symbol ?? " " }, 1000);
            lock (SConsoleLock)
            {
                Render(terminator);
                if (_enabled)
                {
                    SRunningSpinners.Remove(_nodeSelf!);
                    if (SRunningSpinners.Count == 0)
                    {
                        ConsoleHelper.SetCursorVisibility(true);
                    }
                }
            }
        }

        public void Succeed(string? text = null)
        {
            Stop(text, ConsoleHelper.ShouldFallback ? SymbolSucceed.Fallback : SymbolSucceed.Default, ConsoleColor.DarkGreen);
        }

        public void Fail(string? text = null)
        {
            Stop(text, ConsoleHelper.ShouldFallback ? SymbolFailed.Fallback : SymbolFailed.Default, ConsoleColor.Red);
        }

        public void Warn(string? text = null)
        {
            Stop(text, ConsoleHelper.ShouldFallback ? SymbolWarn.Fallback : SymbolWarn.Default, ConsoleColor.DarkYellow);
        }

        public void Info(string? text = null)
        {
            Stop(text, ConsoleHelper.ShouldFallback ? SymbolInfo.Fallback : SymbolInfo.Default, ConsoleColor.Blue);
        }

        public static void Start(string text, Action action, Pattern? pattern = null, Pattern? fallbackPattern = null)
        {
            Start(text, _ => action(), pattern, fallbackPattern);
        }

        public static void Start(string text, Action<Spinner> action, Pattern? pattern = null, Pattern? fallbackPattern = null)
        {
            using (var spinner = new Spinner(text, pattern, fallbackPattern: fallbackPattern))
            {
                spinner.Start();

                try
                {
                    action(spinner);

                    if (spinner.Stopped == false)
                    {
                        spinner.Succeed();
                    }
                }
                catch
                {
                    if (spinner.Stopped == false)
                    {
                        spinner.Fail();
                    }
                    throw;
                }
            }
        }

        public static Task StartAsync(string text, Func<Task> action, Pattern? pattern = null, Pattern? fallbackPattern = null)
        {
            return StartAsync(text, _ => action(), pattern, fallbackPattern);
        }

        public static async Task StartAsync(string text, Func<Spinner, Task> action, Pattern? pattern = null, Pattern? fallbackPattern = null)
        {
            using (var spinner = new Spinner(text, pattern, fallbackPattern: fallbackPattern))
            {
                spinner.Start();

                try
                {
                    await action(spinner).ConfigureAwait(false);
                    if (spinner.Stopped == false)
                    {
                        spinner.Succeed();
                    }
                }
                catch
                {
                    if (spinner.Stopped == false)
                    {
                        spinner.Fail();
                    }
                    throw;
                }
            }
        }

        public static Task<TResult> StartAsync<TResult>(string text, Func<Task<TResult>> action, Pattern? pattern = null, Pattern? fallbackPattern = null)
        {
            return StartAsync(text, _ => action(), pattern, fallbackPattern);
        }

        public static async Task<TResult> StartAsync<TResult>(string text, Func<Spinner, Task<TResult>> action, Pattern? pattern = null, Pattern? fallbackPattern = null)
        {
            using (var spinner = new Spinner(text, pattern, fallbackPattern: fallbackPattern))
            {
                spinner.Start();

                try
                {
                    var result = await action(spinner).ConfigureAwait(false);
                    if (spinner.Stopped == false)
                    {
                        spinner.Succeed();
                    }

                    return result;
                }
                catch
                {
                    if (spinner.Stopped == false)
                    {
                        spinner.Fail();
                    }
                    throw;
                }
            }
        }
    }
}
