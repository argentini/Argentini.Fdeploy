namespace Argentini.Fdeploy.Domain;

public sealed class ServerConnectionSettings
{
    public string ServerAddress { get; set; } = string.Empty;
    public string ShareName { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int ResponseTimeoutMs { get; set; } = 15000;
}