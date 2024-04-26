namespace Argentini.Fdeploy.Domain;

public sealed class LocalFileObject : FileObject
{
    public bool IsStaticFilePath { get; }
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

        AbsoluteServerPath = $"{appState.Settings.Paths.RemoteRootPath}\\{RelativeComparablePath}".FormatServerPath(appState);
 
        foreach (var staticFilePath in appState.Settings.Paths.StaticFilePaths)
        {
            if (RelativeComparablePath.StartsWith(staticFilePath) == false)
                continue;

            IsStaticFilePath = true;
            return;
        }
    }
}