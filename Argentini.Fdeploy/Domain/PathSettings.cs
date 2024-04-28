namespace Argentini.Fdeploy.Domain;

public sealed class PathSettings
{
    public string RemoteRootPath { get; set; } = string.Empty;

    public List<string> CopyFilesToPublishFolder { get; set; } = [];
    public List<string> CopyFoldersToPublishFolder { get; set; } = [];
    
    public List<string> SafeCopyFolderPaths { get; set; } = [];
    public List<string> SafeCopyFilePaths { get; set; } = [];

    public List<string> IgnoreFolderPaths { get; set; } = [];
    public List<string> IgnoreFilePaths { get; set; } = [];
    public List<string> IgnoreFoldersNamed { get; set; } = [];
    public List<string> IgnoreFilesNamed { get; set; } = [];
}