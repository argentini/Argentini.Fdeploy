namespace Argentini.Fdeploy.Domain;

public sealed class ServerConnectionSettings
{
    public string RemoteRootPath { get; set; } = string.Empty;
    public string ServerAddress { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int ConnectTimeoutMs { get; set; } = 5000;
    public int ResponseTimeoutMs { get; set; } = 15000;
}