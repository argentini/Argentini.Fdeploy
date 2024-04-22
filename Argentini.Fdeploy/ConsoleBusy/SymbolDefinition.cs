namespace Argentini.Fdeploy.ConsoleBusy
{
    public class SymbolDefinition(string defaultValue, string fallback)
    {
        public string Default { get; } = defaultValue;
        public string Fallback { get; } = fallback;
    }
}
