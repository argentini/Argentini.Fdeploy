namespace Argentini.Fdeploy.Domain;

public class PathSettings
{
    public string RemoteRootPath { get; set; } = string.Empty;

    public IEnumerable<string> StaticFilePaths { get; set; } = [];
    public IEnumerable<string> RelativeIgnorePaths { get; set; } = [];
    public IEnumerable<string> RelativeIgnoreFilePaths { get; set; } = [];
    public IEnumerable<string> IgnoreFoldersNamed { get; set; } = [];
    public IEnumerable<string> IgnoreFilesNamed { get; set; } = [];
    
    public IEnumerable<FileCopySettings> StaticFileCopies { get; set; } = [];
    public IEnumerable<FileCopySettings> FileCopies { get; set; } = [];
}