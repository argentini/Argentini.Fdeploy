namespace Argentini.Fdeploy.ConsoleBusy
{
    internal static class PInvoke
    {
        private const string Kernel32 = "kernel32.dll";

        internal enum StdHandle : int
        {
            StdOutputHandle = -11,
        }

        [Flags]
        internal enum ConsoleMode : int
        {
            EnableVirtualTerminalProcessing = 0x0004,
        }

        [DllImport(Kernel32)]
        internal static extern IntPtr GetStdHandle(StdHandle nStdHandle);

        [DllImport(Kernel32, SetLastError = true)]
        internal static extern bool GetConsoleMode(IntPtr handle, out ConsoleMode mode);

        [DllImport(Kernel32, SetLastError = true)]
        internal static extern bool SetConsoleMode(IntPtr handle, ConsoleMode mode);
    }
}
