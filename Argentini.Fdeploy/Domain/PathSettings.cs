namespace Argentini.Fdeploy.Domain;

public sealed class PathSettings
{
    public string RemoteRootPath { get; set; } = string.Empty;

    public List<string> StaticFilePaths { get; set; } = [];
    public List<string> RelativeIgnorePaths { get; set; } = [];
    public List<string> RelativeIgnoreFilePaths { get; set; } = [];
    public List<string> IgnoreFoldersNamed { get; set; } = [];
    public List<string> IgnoreFilesNamed { get; set; } = [];
    
    public List<FileCopySettings> StaticFileCopies { get; set; } = [];
    public List<FileCopySettings> FileCopies { get; set; } = [];
}