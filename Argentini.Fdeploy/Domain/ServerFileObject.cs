namespace Argentini.Fdeploy.Domain;

public sealed class ServerFileObject: FileObject
{
    public ServerFileObject(AppState appState, string absolutePath, long lastWriteTime, long fileSizeBytes, bool isFile, string rootPath)
    {
        AbsolutePath = $"\\{absolutePath.FormatServerPath(appState)}";
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