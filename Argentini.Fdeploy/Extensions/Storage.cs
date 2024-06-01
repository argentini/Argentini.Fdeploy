namespace Argentini.Fdeploy.Extensions;

public static class Storage
{
    #region Local Storage
    
    public static async ValueTask RecurseLocalPathAsync(AppState appState, string path)
    {
        foreach (var subdir in Directory.GetDirectories(path).OrderBy(d => d))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                continue;
            
            var fo = new LocalFileObject(appState, subdir, directory.CreationTime.ComparableTime(), directory.LastWriteTime.ComparableTime(), 0, false);
            
            if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                continue;

            if (appState.CurrentSpinner is not null)
                appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {fo.RelativeComparablePath}...";

            appState.LocalFiles.Add(fo);

            await RecurseLocalPathAsync(appState, subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path).OrderBy(f => f))
        {
            try
            {
                var file = new FileInfo(filePath);

                if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    continue;

                var fo = new LocalFileObject(appState, filePath, file.CreationTime.ComparableTime(), file.LastWriteTime.ComparableTime(), file.Length, true);

                if (FilePathShouldBeIgnoredDuringScan(appState, fo))
                    continue;
                
                appState.LocalFiles.Add(fo);
            }
            catch
            {
                appState.Exceptions.Add($"Could process local file `{filePath}`");
                await appState.CancellationTokenSource.CancelAsync();
            }
        }
    }

    public static async Task CopyLocalFolderAsync(AppState appState, string localSourcePath, string localDestinationPath)
    {
        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(localSourcePath);

        if (dir.Exists == false)
        {
            appState.Exceptions.Add($"Could not find source folder `{localSourcePath}`");
            await appState.CancellationTokenSource.CancelAsync();
            return;
        }

        try
        {
            // If the destination directory doesn't exist, create it.       
            Directory.CreateDirectory(localDestinationPath);
        }
        catch
        {
            appState.Exceptions.Add($"Could not create local folder `{localDestinationPath}`");
            await appState.CancellationTokenSource.CancelAsync();
            return;
        }

        // Get the files in the directory and copy them to the new location.
        var files = dir.GetFiles();
        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(appState.Settings.MaxThreadCount);

        foreach (var file in files)
        {
            await semaphore.WaitAsync();
            
            tasks.Add(Task.Run(() =>
            {
                var tempPath = string.Empty;
                
                try
                {
                    if (appState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    tempPath = Path.Combine(localDestinationPath, file.Name);

                    file.CopyTo(tempPath, true);
                }
                catch
                {
                    appState.Exceptions.Add($"Could not copy local file `{tempPath}`");
                    appState.CancellationTokenSource.Cancel();
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var dirs = dir.GetDirectories();

        tasks.Clear();

        foreach (var subdir in dirs)
        {
            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (appState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    await CopyLocalFolderAsync(appState, subdir.FullName, Path.Combine(localDestinationPath, subdir.Name));
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);
    }
    
    #endregion
    
    #region Ignore Rules
    
    public static bool FolderPathShouldBeIgnoredDuringScan(AppState appState, FileObject fo)
    {
        foreach (var ignorePath in appState.Settings.Paths.IgnoreFolderPaths)
        {
            if (fo.RelativeComparablePath != ignorePath)
                continue;

            return true;
        }

        return appState.Settings.Paths.IgnoreFoldersNamed.Contains(fo.FileNameOrPathSegment);
    }

    public static bool FilePathShouldBeIgnoredDuringScan(AppState appState, FileObject fo)
    {
        foreach (var ignorePath in appState.Settings.Paths.IgnoreFilePaths)
        {
            if (fo.RelativeComparablePath != ignorePath)
                continue;

            return true;
        }

        return appState.Settings.Paths.IgnoreFilesNamed.Contains(fo.FileNameOrPathSegment) || fo.FileNameOrPathSegment == "app_offline.htm";
    }
    
    #endregion
    
    #region Server Storage
    
    public static async Task RecurseServerPathAsync(this AppState appState, string path)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var files = Directory.GetFiles(path).ToList();

        #region Files

        if (files.Count != 0)
        {
            foreach (var file in files.Order())
            {
                if (appState.CancellationTokenSource.IsCancellationRequested)
                    return;

                var fileInfo = new FileInfo(file);

                try
                {
                    var fo = new ServerFileObject(appState, file, fileInfo.CreationTime.ComparableTime(), fileInfo.LastWriteTime.ComparableTime(), fileInfo.Length, true);

                    if (FilePathShouldBeIgnoredDuringScan(appState, fo))
                        continue;

                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {fo.RelativeComparablePath}...";

                    appState.ServerFiles.Add(fo);
                }
                catch
                {
                    appState.Exceptions.Add($"Could not process server file `{fileInfo.Name}`");
                    await appState.CancellationTokenSource.CancelAsync();
                }
            }
        }

        #endregion

        #region Directories

        var directories = Directory.GetDirectories(path).ToList();

        if (directories.Count == 0)
            return;

        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(appState.Settings.MaxThreadCount);
        
        foreach (var directory in directories.OrderBy(f => f.GetLastPathSegment()))
        {
            await semaphore.WaitAsync();

            tasks.Add(Task.Run(async () =>
            {
                var dirInfo = new DirectoryInfo(directory);

                try
                {
                    if (appState.CancellationTokenSource.IsCancellationRequested)
                        return;

                    var fo = new ServerFileObject(appState, directory, dirInfo.CreationTime.ComparableTime(), dirInfo.LastWriteTime.ComparableTime(), 0, false);

                    if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                        return;

                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {fo.RelativeComparablePath}...";

                    appState.ServerFiles.Add(fo);

                    await RecurseServerPathAsync(appState, directory);
                }
                catch
                {
                    appState.Exceptions.Add($"Could not index server directory `{dirInfo.Name}`");
                    await appState.CancellationTokenSource.CancelAsync();
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        #endregion
    }
    
    public static bool ServerFileExists(this AppState appState, LocalFileObject fo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        return File.Exists(fo.AbsoluteServerPath);
    }

    public static bool ServerFileExists(this AppState appState, ServerFileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        return File.Exists(sfo.AbsolutePath);
    }

    public static bool ServerFolderExists(this AppState appState, string? serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        return Directory.Exists(serverFolderPath);
    }

    public static void EnsureServerPathExists(this AppState appState, string? serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (appState.ServerFolderExists(serverFolderPath))
            return;

        var success = false;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

        if (string.IsNullOrEmpty(serverFolderPath))
        {
            appState.Exceptions.Add("Server directory name is blank");
            appState.CancellationTokenSource.Cancel();
            return;
        }
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            Directory.CreateDirectory(serverFolderPath);

            if (appState.ServerFolderExists(serverFolderPath))
            {
                success = true;
                break;
            }

            success = false;

            for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
            {
                if (appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {serverFolderPath} Retry {attempt + 1} ({x:N0})...";

                Thread.Sleep(1000);
            }
        }

        if (success)
            return;
        
        appState.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFolderPath}`");
        appState.CancellationTokenSource.Cancel();
    }

    public static void DeleteServerFile(this AppState appState, LocalFileObject fo, bool showOutput = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (appState.ServerFileExists(fo) == false)
            return;

        var serverRelativePath = fo.RelativeComparablePath;

        if (showOutput && appState.CurrentSpinner is not null)
            appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {serverRelativePath}...";

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                File.Delete(fo.AbsoluteServerPath);

                success = appState.ServerFileExists(fo) == false;
            }
            catch
            {
                success = false;
            }

            if (success)
                break;

            for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
            {
                if (appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text =
                        $"{appState.CurrentSpinner.OriginalText} {serverRelativePath}... Retry {attempt + 1} ({x:N0})...";

                Thread.Sleep(1000);
            }
        }

        if (success)
            return;

        appState.Exceptions.Add(
            $"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{fo.RelativeComparablePath}`");
        appState.CancellationTokenSource.Cancel();
    }

    public static void DeleteServerFile(this AppState appState, ServerFileObject sfo, bool showOutput = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (appState.ServerFileExists(sfo) == false)
            return;
        
        var serverRelativePath = sfo.RelativeComparablePath;
        
        if (showOutput && appState.CurrentSpinner is not null)
            appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {serverRelativePath}...";
        
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                File.Delete(sfo.AbsolutePath);

                success = appState.ServerFileExists(sfo) == false;
            }
            catch
            {
                success = false;
            }

            if (success)
                break;

            for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
            {
                if (appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {serverRelativePath}... Retry {attempt + 1} ({x:N0})...";

                Thread.Sleep(1000);
            }
        }

        if (success)
            return;

        appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{sfo.RelativeComparablePath}`");
        appState.CancellationTokenSource.Cancel();
    }

    public static void DeleteServerFolder(this AppState appState, ServerFileObject sfo, bool showOutput = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (showOutput && appState.CurrentSpinner is not null)
            appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {sfo.RelativeComparablePath}...";
        
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            Directory.Delete(sfo.AbsolutePath, true);

            success = appState.ServerFolderExists(sfo.AbsolutePath) == false;

            if (success)
                break;

            for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
            {
                if (appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {sfo.RelativeComparablePath}... Retry {attempt + 1} ({x:N0})...";

                Thread.Sleep(1000);
            }
        }

        if (success)
            return;
        
        appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{sfo.AbsolutePath}`");
        appState.CancellationTokenSource.Cancel();
    }

    public static void CopyFile(this AppState appState, LocalFileObject fo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        try
        {
            if (File.Exists(fo.AbsolutePath) == false)
            {
                appState.Exceptions.Add($"File `{fo.RelativeComparablePath}` does not exist");
                appState.CancellationTokenSource.Cancel();
                return;
            }
         
            if (appState.CurrentSpinner is not null)
                appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {fo.RelativeComparablePath}...";
            
            appState.EnsureServerPathExists(fo.AbsoluteServerPath.TrimEnd(fo.AbsoluteServerPath.GetLastPathSegment()));
            
            if (appState.CancellationTokenSource.IsCancellationRequested)
                return;

            try
            {
                var success = true;
                var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

                appState.DeleteServerFile(fo);

                if (appState.CancellationTokenSource.IsCancellationRequested)
                    return;
                
                for (var attempt = 0; attempt < retries; attempt++)
                {
                    File.Copy(fo.AbsolutePath, fo.AbsoluteServerPath, overwrite: true);

                    var serverFileInfo = new FileInfo(fo.AbsoluteServerPath);

                    if (fo.FileSizeBytes != serverFileInfo.Length)
                        success = false;

                    if (success)
                        break;

                    for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
                    {
                        if (appState.CurrentSpinner is not null)
                            appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} {fo.RelativeComparablePath} Retry {attempt + 1} ({x:N0})...";

                        Thread.Sleep(1000);
                    }
                }
                
                if (success)
                    return;
                
                appState.Exceptions.Add($"Failed to copy file {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{fo.RelativeComparablePath}`");
                appState.CancellationTokenSource.Cancel();
            }
            catch (Exception e)
            {
                appState.Exceptions.Add($"Failed to copy file `{fo.RelativeComparablePath}`; {e.Message}");
                appState.CancellationTokenSource.Cancel();
            }
        }
        catch (Exception e)
        {
            appState.Exceptions.Add($"Failed to copy file `{fo.RelativeComparablePath}`; {e.Message}");
            appState.CancellationTokenSource.Cancel();
        }
    }

    #endregion
    
    #region Offline Support
    
    public static void TakeServerOffline(this AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var fo = new LocalFileObject(appState, $"{appState.PublishPath}{Path.DirectorySeparatorChar}app_offline.htm", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), appState.AppOfflineMarkup.Length, true);
        
        appState.DeleteServerFile(fo);

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        try
        {
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

            if (appState.CurrentSpinner is not null)
                appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} Creating /app_offline.htm...";

            for (var attempt = 0; attempt < retries; attempt++)
            {
                File.WriteAllText(fo.AbsoluteServerPath, appState.AppOfflineMarkup);

                success = File.Exists(fo.AbsoluteServerPath);

                if (success)
                    break;

                for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
                {
                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.OriginalText} Creating /app_offline.htm... Retry {attempt + 1} ({x:N0})...";

                    Thread.Sleep(1000);
                }
            }

            if (success)
                return;
            
            appState.Exceptions.Add($"Failed to create offline file {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{fo.RelativeComparablePath}`");
            appState.CancellationTokenSource.Cancel();
        }
        catch
        {
            appState.Exceptions.Add($"Failed to create offline file `{fo.RelativeComparablePath}`");
            appState.CancellationTokenSource.Cancel();
        }
    }
    
    public static void BringServerOnline(this AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var fo = new LocalFileObject(appState, $"{appState.PublishPath}{Path.DirectorySeparatorChar}app_offline.htm", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), appState.AppOfflineMarkup.Length, true);
        
        appState.DeleteServerFile(fo);
    }
    
    #endregion
}