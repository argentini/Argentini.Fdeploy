namespace Argentini.Fdeploy.ConsoleBusy
{
    public class Patterns
    {
        public static readonly Pattern Dots = new([
            "⠋",
            "⠙",
            "⠹",
            "⠸",
            "⠼",
            "⠴",
            "⠦",
            "⠧",
            "⠇",
            "⠏"
        ], interval: 80);

        public static readonly Pattern Line = new([
            "-",
            "\\",
            "|",
            "/"
        ], interval: 130);
    }

    public class Pattern(string[] frames, int interval)
    {
        public string[] Frames { get; } = frames;
        public int Interval { get; } = interval;
    }
}
