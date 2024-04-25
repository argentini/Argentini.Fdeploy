using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary.Client;

namespace Argentini.Fdeploy.Domain;

public sealed class AppState
{
    public string AppOfflineMarkup { get; set; } = string.Empty;
    public string YamlProjectFilePath { get; set; } = string.Empty;
    public string WorkingPath { get; set; } = string.Empty;
    public string PublishPath { get; set; } = string.Empty;
    public string TrimmablePublishPath { get; set; } = string.Empty;

    public SMB2Client Client { get; set; } = new();
    public ISMBFileStore? FileStore { get; set; }
    public Settings Settings { get; set; } = new();
    public List<string> Exceptions { get; set; } = [];
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public Spinner? CurrentSpinner { get; set; }
    public List<ServerFileObject> ServerFiles { get; set; } = [];
    public List<LocalFileObject> LocalFiles { get; set; } = [];
    
}