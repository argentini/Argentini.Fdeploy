namespace Argentini.Fdeploy.Domain;

public sealed class ServerFileObject: FileObject
{
    public ServerFileObject(string absolutePath, long lastWriteTime, long fileSizeBytes, bool isFile, string rootPath)
    {
        AbsolutePath = $"\\{absolutePath.TrimPath()}";
        FileNameOrPathSegment = AbsolutePath.GetLastPathSegment();
        ParentPath = AbsolutePath.TrimEnd(FileNameOrPathSegment)?.TrimEnd('\\') ?? string.Empty;
        RelativeComparablePath = AbsolutePath.SetNativePathSeparators().TrimPath().TrimStart(rootPath.SetNativePathSeparators().TrimPath()).TrimPath();

        LastWriteTime = lastWriteTime;
        FileSizeBytes = fileSizeBytes;
        IsFile = isFile;
        IsFolder = isFile == false;

        SetPathSegments();
    }
}