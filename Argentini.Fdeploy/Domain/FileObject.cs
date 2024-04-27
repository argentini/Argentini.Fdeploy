using System.Diagnostics.CodeAnalysis;

namespace Argentini.Fdeploy.Domain;

[SuppressMessage("ReSharper", "MemberCanBeProtected.Global")]
public abstract class FileObject
{
    public string AbsolutePath { get; protected set; } = string.Empty;
    public string ParentPath { get; protected set; } = string.Empty;
    public string FileNameOrPathSegment { get; protected set; } = string.Empty;
    public string RelativeComparablePath { get; protected set; } = string.Empty;

    public long LastWriteTime { get; protected set; }
    public long FileSizeBytes { get; protected set; }
    public bool IsFile { get; protected set; }
    public bool IsFolder { get; protected set; }
    public bool IsDeleted { get; set; }

    public List<string> PathSegments { get; } = [];

    public int Level => PathSegments.Count;

    protected void SetPathSegments()
    {
        if (string.IsNullOrEmpty(AbsolutePath))
            return;

        var separator = AbsolutePath.Contains('/') ? '/' : '\\'; 
            
        PathSegments.AddRange(ParentPath.Split(separator, StringSplitOptions.RemoveEmptyEntries));
    }
}