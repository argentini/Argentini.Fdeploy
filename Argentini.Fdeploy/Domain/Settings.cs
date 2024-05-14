namespace Argentini.Fdeploy.Domain;

public sealed class Settings
{
    public bool DeleteOrphans { get; set; } = true;
    public bool TakeServerOffline { get; set; } = true;
    public bool CompareFileDates { get; set; } = true;
    public bool CompareFileSizes { get; set; } = true;
    
    public int ServerOfflineDelaySeconds { get; set; }
    public int ServerOnlineDelaySeconds { get; set; }
    public int WriteRetryDelaySeconds { get; set; } = 10;
    public int RetryCount { get; set; } = 10;
    public int MaxThreadCount { get; set; } = Environment.ProcessorCount;

    public bool CleanProject { get; set; } = true;
    public bool PurgeProject { get; set; } = true;

    public ServerConnectionSettings ServerConnection { get; set; } = new();
    public ProjectSettings Project { get; set; } = new();
    public PathsSettings Paths { get; set; } = new();
    public OfflineSettings Offline { get; set; } = new();
}
