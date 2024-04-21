namespace Argentini.Fdeploy.Domain;

public sealed class FdeploySettings
{
    public bool DeleteOrphans { get; set; } = true;
    public bool OverwriteWebConfig { get; set; } = true;
    public bool TakeServerOffline { get; set; } = true;
    
    public int ServerOfflineDelaySeconds { get; set; } = 10;
    public int WriteRetryDelaySeconds { get; set; } = 10;
    
    public ServerConnectionSettings ServerConnection { get; set; } = new();
    public ProjectSettings Project { get; set; } = new();
    public PathSettings Paths { get; set; } = new();
}
