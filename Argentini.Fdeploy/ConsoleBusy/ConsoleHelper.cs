namespace Argentini.Fdeploy.ConsoleBusy
{
    internal static class ConsoleHelper
    {
        public static bool ShouldFallback =>
            Environment.OSVersion.Platform == PlatformID.Win32NT &&
            (Console.OutputEncoding.CodePage != 1200 /* UTF-16 */ && Console.OutputEncoding.CodePage != 65001 /* UTF-8 */);

        private static bool _canAcceptEscapeSequence = (Environment.OSVersion.Platform != PlatformID.Win32NT);
        public static bool CanAcceptEscapeSequence
            => _canAcceptEscapeSequence && IsRunningOnCi == false && Console.IsOutputRedirected == false;

        public static bool IsRunningOnCi { get; } = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CI")) == false;

        public static bool TryEnableEscapeSequence()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return false;
            
            var stdOutput = PInvoke.GetStdHandle(PInvoke.StdHandle.StdOutputHandle);

            if (PInvoke.GetConsoleMode(stdOutput, out var mode) == false) return false;

            if (PInvoke.SetConsoleMode(stdOutput, mode | PInvoke.ConsoleMode.EnableVirtualTerminalProcessing) == false)
                return false;
            
            _canAcceptEscapeSequence = true;

            return true;
        }

        public static void WriteWithColor(string s, ConsoleColor color)
        {
            var foregroundColor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.Write(s);
            Console.ForegroundColor = foregroundColor;
        }

        // See: https://stackoverflow.com/questions/8946808/can-console-clear-be-used-to-only-clear-a-line-instead-of-whole-console/8946847#8946847
        public static void ClearCurrentConsoleLine(int length, int top)
        {
            if (Console.IsOutputRedirected) return;

            if (CanAcceptEscapeSequence)
            {
                Console.SetCursorPosition(0, top);
                Console.Write("\u001B[2K");
            }
            else
            {
                Console.SetCursorPosition(0, top);
                Console.Write(new string(' ', length));
                Console.SetCursorPosition(0, top);
            }
            Console.Out.Flush();
        }

        public static void SetCursorVisibility(bool visible)
        {
            if (CanAcceptEscapeSequence == false) return;

            if (visible)
            {
                Console.Write("\u001B[?25h");
            }
            else
            {
                Console.Write("\u001B[?25l");
            }
        }
    }
}
