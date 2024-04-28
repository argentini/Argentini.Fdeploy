using System.Diagnostics.CodeAnalysis;

namespace Argentini.Fdeploy.Domain;

[SuppressMessage("ReSharper", "CollectionNeverUpdated.Global")]
public sealed class DeploymentSettings
{
    public List<string> OnlineCopyFolderPaths { get; set; } = [];
    public List<string> OnlineCopyFilePaths { get; set; } = [];

    public List<string> IgnoreFolderPaths { get; set; } = [];
    public List<string> IgnoreFilePaths { get; set; } = [];
    public List<string> IgnoreFoldersNamed { get; set; } = [];
    public List<string> IgnoreFilesNamed { get; set; } = [];
}