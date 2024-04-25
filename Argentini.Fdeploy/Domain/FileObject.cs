namespace Argentini.Fdeploy.Domain;

public sealed class FileObject
{
    public required string FullPath { get; set; } = string.Empty;
    public required long LastWriteTime { get; set; }
    public required long FileSizeBytes { get; set; }

    public string FilePath { get; set; } = string.Empty;
    public string RelativeComparablePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public bool IsFile { get; set; }
    public bool IsFolder { get; set; }
    
    private string[]? _pathSegments;
    public string[] PathSegments
    {
        get
        {
            if (string.IsNullOrEmpty(FilePath))
                return [];

            return _pathSegments ??= FilePath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        }
    }

    public int Level => PathSegments.Length;
}