namespace Argentini.Fdeploy.Domain;

public sealed class Credentials
{
    public string Domain { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}