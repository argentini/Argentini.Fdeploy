namespace Argentini.Fdeploy.Domain;

public sealed class LocalFileObject : FileObject
{
    public bool IsOnlineCopy { get; }
    public string AbsoluteServerPath { get; }

    public LocalFileObject(AppState appState, string absolutePath, long lastWriteTime, long fileSizeBytes, bool isFile, string rootPath)
    {
        AbsolutePath = $"{Path.DirectorySeparatorChar}{absolutePath.FormatLocalPath(appState)}";
        FileNameOrPathSegment = AbsolutePath.GetLastPathSegment();
        ParentPath = AbsolutePath.TrimEnd(FileNameOrPathSegment)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        RelativeComparablePath = AbsolutePath.TrimPath().TrimStart(rootPath.TrimPath()).TrimPath();

        LastWriteTime = lastWriteTime;
        FileSizeBytes = fileSizeBytes;
        IsFile = isFile;
        IsFolder = isFile == false;

        SetPathSegments();

        AbsoluteServerPath = $"{appState.Settings.ServerConnection.RemoteRootPath}\\{RelativeComparablePath}".FormatServerPath(appState);
        
        foreach (var staticFolderPath in appState.Settings.Paths.OnlineCopyFolderPaths)
        {
            if (RelativeComparablePath.StartsWith(staticFolderPath) == false)
                continue;

            IsOnlineCopy = true;
            return;
        }
        
        foreach (var staticFilePath in appState.Settings.Paths.OnlineCopyFilePaths)
        {
            if (RelativeComparablePath != staticFilePath)
                continue;

            IsOnlineCopy = true;
            return;
        }
    }
}