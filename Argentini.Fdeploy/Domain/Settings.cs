namespace Argentini.Fdeploy.Domain;

public sealed class Settings
{
    public bool DeleteOrphans { get; set; } = true;
    public bool TakeServerOffline { get; set; } = true;
    
    public int ServerOfflineDelaySeconds { get; set; }
    public int ServerOnlineDelaySeconds { get; set; }
    public int WriteRetryDelaySeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 10;
    public int MaxThreadCount { get; set; } = Environment.ProcessorCount;
    
    public ServerConnectionSettings ServerConnection { get; set; } = new();
    public ProjectSettings Project { get; set; } = new();
    public DeploymentSettings Deployment { get; set; } = new();
    public AppOfflineSettings AppOffline { get; set; } = new();
}
