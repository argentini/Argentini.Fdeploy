namespace Argentini.Fdeploy.Domain;

public sealed class LocalFileObject : FileObject
{
    public LocalFileObject(string absolutePath, long lastWriteTime, long fileSizeBytes, bool isFile, string rootPath)
    {
        AbsolutePath = $"{Path.DirectorySeparatorChar}{absolutePath.TrimPath()}";
        FileNameOrPathSegment = AbsolutePath.GetLastPathSegment();
        ParentPath = AbsolutePath.TrimEnd(FileNameOrPathSegment)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        RelativeComparablePath = AbsolutePath.TrimPath().TrimStart(rootPath.TrimPath()).TrimPath();

        LastWriteTime = lastWriteTime;
        FileSizeBytes = fileSizeBytes;
        IsFile = isFile;
        IsFolder = isFile == false;

        SetPathSegments();
    }
}