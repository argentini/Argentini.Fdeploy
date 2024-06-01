namespace Argentini.Fdeploy.Domain;

public sealed class LocalFileObject : FileObject
{
    public bool IsOnlineCopy { get; }
    public bool AlwaysOverwrite { get; }
    public string AbsoluteServerPath { get; }

    public LocalFileObject(AppState appState, string absolutePath, long createTime, long lastWriteTime, long fileSizeBytes, bool isFile)
    {
        var formattedPath = absolutePath.MakeRelativePath().TrimStart(appState.TrimmablePublishPath).MakeRelativePath();
        
        AbsolutePath = $"{appState.PublishPath}{(string.IsNullOrEmpty(formattedPath) == false ? $"{Path.DirectorySeparatorChar}{formattedPath}" : string.Empty)}";
        FileNameOrPathSegment = AbsolutePath.GetLastPathSegment();
        ParentPath = AbsolutePath.TrimEnd(FileNameOrPathSegment)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        RelativeComparablePath = AbsolutePath.MakeRelativePath().TrimStart(appState.PublishPath.MakeRelativePath()).MakeRelativePath();

        CreateTime = createTime;
        LastWriteTime = lastWriteTime;
        FileSizeBytes = fileSizeBytes;
        IsFile = isFile;
        IsFolder = isFile == false;

        SetPathSegments();

        if (IsFile && appState.Settings.Paths.AlwaysOverwritePaths.Count != 0)
        {
            foreach (var alwaysOverwritePath in appState.Settings.Paths.AlwaysOverwritePaths)
            {
                var relativeParentPath = ParentPath.TrimStart(appState.PublishPath).MakeRelativePath(); 
                var overwriteAtRoot = alwaysOverwritePath is "" or "~" && relativeParentPath == string.Empty;

                if (relativeParentPath.InvariantEquals(alwaysOverwritePath.Replace("~", string.Empty)) == false && overwriteAtRoot == false)
                    continue;

                AlwaysOverwrite = true;
                break;
            }
        }

        if (IsFile && appState.Settings.Paths.AlwaysOverwritePathsWithRecurse.Count != 0)
        {
            foreach (var alwaysOverwritePath in appState.Settings.Paths.AlwaysOverwritePathsWithRecurse)
            {
                var relativeParentPath = ParentPath.TrimStart(appState.PublishPath).MakeRelativePath();

                if (alwaysOverwritePath is "" or "~")
                {
                    AlwaysOverwrite = true;
                    break;
                }
                
                if (relativeParentPath.InvariantNotEquals(alwaysOverwritePath) && (relativeParentPath + Path.DirectorySeparatorChar).StartsWith(alwaysOverwritePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) == false)
                    continue;

                AlwaysOverwrite = true;
                break;
            }
        }
        
        AbsoluteServerPath = $"{appState.GetServerPathPrefix()}{Path.DirectorySeparatorChar}{RelativeComparablePath.MakeRelativePath()}";
        
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