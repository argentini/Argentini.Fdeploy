namespace Argentini.Fdeploy.Domain;

public sealed class ServerFileObject: FileObject
{
    public ServerFileObject(AppState appState, string absolutePath, long createTime, long lastWriteTime, long fileSizeBytes, bool isFile)
    {
        AbsolutePath = absolutePath;
        FileNameOrPathSegment = AbsolutePath.GetLastPathSegment();
        ParentPath = AbsolutePath.TrimEnd(FileNameOrPathSegment)?.TrimEnd('\\') ?? string.Empty;
        RelativeComparablePath = AbsolutePath.MakeRelativePath().TrimStart(appState.GetServerPathPrefix().MakeRelativePath()).MakeRelativePath();

        CreateTime = createTime;
        LastWriteTime = lastWriteTime;
        FileSizeBytes = fileSizeBytes;
        IsFile = isFile;
        IsFolder = isFile == false;

        SetPathSegments();
    }
}