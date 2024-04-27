using System.Net.Sockets;
using SMBLibrary;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Argentini.Fdeploy.Extensions;

public static class Storage
{
    #region Local Storage
    
    public static async ValueTask RecurseLocalPathAsync(AppState appState, string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                continue;
            
            var fo = new LocalFileObject(appState, subdir, directory.LastWriteTime.ToFileTimeUtc(), 0, false, appState.PublishPath);
            
            if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                continue;

            appState.LocalFiles.Add(fo);

            await RecurseLocalPathAsync(appState, subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path))
        {
            try
            {
                var file = new FileInfo(filePath);

                if ((file.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                    continue;

                var fo = new LocalFileObject(appState, filePath, file.LastWriteTime.ToFileTimeUtc(), file.Length, true, appState.PublishPath);
            
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

    public static async ValueTask CopyFolderAsync(string localSourcePath, string localDestinationPath)
    {
        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(localSourcePath);

        if (dir.Exists == false)
        {
            throw new DirectoryNotFoundException(
                "Source directory does not exist or could not be found: "
                + localSourcePath);
        }

        var dirs = dir.GetDirectories();
    
        // If the destination directory doesn't exist, create it.       
        Directory.CreateDirectory(localDestinationPath);        

        // Get the files in the directory and copy them to the new location.
        var files = dir.GetFiles();
        
        foreach (var file in files)
        {
            var tempPath = Path.Combine(localDestinationPath, file.Name);
            file.CopyTo(tempPath, false);
        }

        // If copying subdirectories, copy them and their contents to new location.
        foreach (var subdir in dirs)
        {
            var tempPath = Path.Combine(localDestinationPath, subdir.Name);
            await CopyFolderAsync(subdir.FullName, tempPath);
        }
    }
    
    #endregion
    
    #region Ignore Rules
    
    public static bool FolderPathShouldBeIgnoredDuringScan(AppState appState, FileObject fo)
    {
        foreach (var ignorePath in appState.Settings.Paths.RelativeIgnorePaths)
        {
            if (fo.RelativeComparablePath != ignorePath)
                continue;

            return true;
        }

        return appState.Settings.Paths.IgnoreFoldersNamed.Contains(fo.FileNameOrPathSegment);
    }

    public static bool FilePathShouldBeIgnoredDuringScan(AppState appState, FileObject fo)
    {
        foreach (var ignorePath in appState.Settings.Paths.RelativeIgnoreFilePaths)
        {
            if (fo.RelativeComparablePath != ignorePath)
                continue;

            return true;
        }

        return appState.Settings.Paths.IgnoreFilesNamed.Contains(fo.FileNameOrPathSegment);
    }
    
    #endregion
    
    #region SMB
    
    public static void Connect(AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var serverAvailable = false;
        
        using (var client = new TcpClient())
        {
            try
            {
                var result = client.BeginConnect(appState.Settings.ServerConnection.ServerAddress, 445, null, null);
                
                serverAvailable = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(appState.Settings.ServerConnection.ConnectTimeoutMs));
            }
            catch
            {
                serverAvailable = false;
            }
            finally
            {
                client.Close();
            }
        }

        if (serverAvailable == false)
        {
            appState.Exceptions.Add("Server is not responding");
            appState.CancellationTokenSource.Cancel();
            return;
        }
        
        var isConnected = appState.Client.Connect(appState.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, appState.Settings.ServerConnection.ResponseTimeoutMs);
        var shares = new List<string>();

        if (isConnected)
        {
            var status = appState.Client.Login(appState.Settings.ServerConnection.Domain, appState.Settings.ServerConnection.UserName, appState.Settings.ServerConnection.Password);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                shares = appState.Client.ListShares(out status);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    if (shares.Contains(appState.Settings.ServerConnection.ShareName, StringComparer.OrdinalIgnoreCase) == false)
                    {
                        appState.Exceptions.Add("Network share not found on the server");
                        appState.CancellationTokenSource.Cancel();
                    }

                    else
                    {
                        EstablishFileStore(appState);
                    }
                }

                else
                {
                    appState.Exceptions.Add("Could not retrieve server shares list");
                    appState.CancellationTokenSource.Cancel();
                }
            }

            else
            {
                appState.Exceptions.Add("Server authentication failed");
                appState.CancellationTokenSource.Cancel();
            }
        }        

        else
        {
            appState.Exceptions.Add("Could not connect to the server");
            appState.CancellationTokenSource.Cancel();
        }
        
        if (appState.CancellationTokenSource.IsCancellationRequested)
            Disconnect(appState);
    }

    public static void Disconnect(AppState appState)
    {
        if (appState.Client.IsConnected == false)
            return;

        try
        {
            appState.Client.Logoff();
        }

        finally
        {
            appState.Client.Disconnect();
        }
    }

    public static void EstablishFileStore(AppState appState)
    {
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            appState.FileStore = appState.Client.TreeConnect(appState.Settings.ServerConnection.ShareName, out var status);

            if (status == NTStatus.STATUS_SUCCESS)
                break;

            Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000).GetAwaiter();
        }

        if (appState.FileStore is not null)
            return;
        
        appState.Exceptions.Add($"Could not connect to the file share `{appState.Settings.ServerConnection.ShareName}`");
        appState.CancellationTokenSource.Cancel();
    }
    
    public static async ValueTask EnsureFileStoreAsync(AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            appState.FileStore = appState.Client.TreeConnect(appState.Settings.ServerConnection.ShareName, out var status);

            if (status == NTStatus.STATUS_SUCCESS)
                break;

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (appState.FileStore is not null)
            return;
        
        appState.Exceptions.Add($"Could not connect to the file share `{appState.Settings.ServerConnection.ShareName}`");
        await appState.CancellationTokenSource.CancelAsync();
    }
    
    #endregion
    
    #region Server Storage
    
    public static async ValueTask RecurseServerPathAsync(AppState appState, string path, bool includeHidden = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var status = NTStatus.STATUS_SUCCESS;
        
        if (appState.FileStore is null)
            return;

        status = appState.FileStore.CreateFile(out var handle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            if (await ServerFolderExistsAsync(appState, path) == false)
                return;
        }

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = appState.FileStore.QueryDirectory(out var fileList, handle, "*", FileInformationClass.FileDirectoryInformation);

            if (handle is not null)
                status = appState.FileStore.CloseFile(handle);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                foreach (var item in fileList)
                {
                    if (appState.CancellationTokenSource.IsCancellationRequested)
                        break;
                    
                    FileDirectoryInformation? file = null;

                    try
                    {
                        file = (FileDirectoryInformation)item;
                        
                        if (file.FileName is "." or "..")
                            continue;

                        var filePath = $"{path}\\{file.FileName}";

                        if (includeHidden == false && (file.FileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                            continue;
                        
                        var isDirectory = (file.FileAttributes & FileAttributes.Directory) == FileAttributes.Directory;
                        
                        if (isDirectory)
                        {
                            if (appState.CurrentSpinner is not null)
                                appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.Text[..appState.CurrentSpinner.Text.IndexOf("...", StringComparison.Ordinal)]}... {file.FileName}/...";

                            var fo = new ServerFileObject(appState, filePath.Trim('\\'), file.LastWriteTime.ToFileTimeUtc(), 0, false, appState.Settings.Paths.RemoteRootPath);
                                
                            if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                                continue;
                            
                            appState.ServerFiles.Add(fo);
                            
                            await RecurseServerPathAsync(appState, filePath);
                        }

                        else
                        {
                            var fo = new ServerFileObject(appState, filePath.Trim('\\'), file.LastWriteTime.ToFileTimeUtc(), file.EndOfFile, true, appState.Settings.Paths.RemoteRootPath);

                            if (FilePathShouldBeIgnoredDuringScan(appState, fo))
                                continue;

                            appState.ServerFiles.Add(fo);
                        }
                    }
                    catch
                    {
                        appState.Exceptions.Add($"Could process server file `{(file is null ? item.ToString() : file.FileName)}`");
                        await appState.CancellationTokenSource.CancelAsync();
                    }
                }
            }
            else
            {
                appState.Exceptions.Add($"Could not read the contents of server path `{path}`");
                await appState.CancellationTokenSource.CancelAsync();
            }
        }
        else
        {
            appState.Exceptions.Add($"Cannot write to server path `{path}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask<bool> ServerFileExistsAsync(AppState appState, string serverFilePath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return false;

        serverFilePath = serverFilePath.FormatServerPath(appState);
        
        var status = appState.FileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (handle is not null)
            appState.FileStore.CloseFile(handle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }
    
    public static async ValueTask<bool> ServerFolderExistsAsync(AppState appState, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return false;
        
        serverFolderPath = serverFolderPath.FormatServerPath(appState);
        
        var status = appState.FileStore.CreateFile(out var handle, out _, serverFolderPath, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (handle is not null)
            appState.FileStore.CloseFile(handle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    public static async ValueTask EnsureServerPathExists(AppState appState, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        serverFolderPath = serverFolderPath.FormatServerPath(appState);

        if (await ServerFolderExistsAsync(appState, serverFolderPath))
            return;

        var segments = serverFolderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;
        const string spinnerText = "Creating server folder...";

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (await ServerFolderExistsAsync(appState, buildingPath))
                continue;
            
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
                if (appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text = $"{spinnerText} `{buildingPath}`...";

                var status = appState.FileStore.CreateFile(out var handle, out _, buildingPath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

                if (handle is not null)
                    appState.FileStore.CloseFile(handle);

                if (status is NTStatus.STATUS_SUCCESS or NTStatus.STATUS_OBJECT_NAME_COLLISION)
                {
                    success = true;
                    break;
                }

                success = false;

                for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
                {
                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} `{buildingPath}`; Retry {attempt + 1} ({x:N0})...";

                    await Task.Delay(1000);
                }
            }

            if (success)
                continue;
            
            appState.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{buildingPath}`");
            await appState.CancellationTokenSource.CancelAsync();
            break;
        }
    }
    
    public static async ValueTask DeleteServerFileAsync(AppState appState, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = appState.FileStore.CreateFile(out var handle, out _, sfo.AbsolutePath.TrimPath(), AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = appState.FileStore.SetFileInformation(handle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
            }
            else
            {
                var fileExists = await ServerFileExistsAsync(appState, sfo.AbsolutePath.TrimPath());
        
                if (fileExists == false)
                    success = true;
                else        
                    success = false;
            }

            if (handle is not null)
                appState.FileStore.CloseFile(handle);

            if (success)
                break;

            if (appState.CurrentSpinner is not null)
            {
                var text = appState.CurrentSpinner.Text;

                if (text.Contains("... Retry", StringComparison.Ordinal))
                    text = appState.CurrentSpinner.Text[..text.IndexOf("... Retry", StringComparison.Ordinal)] + "...";

                appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1} ({sfo.FileNameOrPathSegment})...";
            }

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{sfo.AbsolutePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }

    public static async ValueTask DeleteServerFolderAsync(AppState appState, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = appState.FileStore.CreateFile(out var handle, out _, sfo.AbsolutePath.TrimPath(), AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = appState.FileStore.SetFileInformation(handle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
            }
            else
            {
                var folderExists = await ServerFolderExistsAsync(appState, sfo.AbsolutePath.TrimPath());

                if (folderExists == false)
                    success = true;
                else                
                    success = false;
            }

            if (handle is not null)
                appState.FileStore.CloseFile(handle);

            if (success)
                break;

            if (appState.CurrentSpinner is not null)
            {
                var text = appState.CurrentSpinner.Text;

                if (text.Contains("... Retry", StringComparison.Ordinal))
                    text = appState.CurrentSpinner.Text[..text.IndexOf("... Retry", StringComparison.Ordinal)] + "...";
                
                appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1} ({sfo.FileNameOrPathSegment}/)...";
            }

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{sfo.AbsolutePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }

    public static async ValueTask DeleteServerFolderRecursiveAsync(AppState appState, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var folderExists = await ServerFolderExistsAsync(appState, sfo.AbsolutePath.TrimPath());
        
        if (folderExists == false)
            return;
        
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        // Delete all files in the path

        foreach (var file in appState.ServerFiles.ToList().Where(f => f.IsFile && f.AbsolutePath.StartsWith(sfo.AbsolutePath)))
        {
            await DeleteServerFileAsync(appState, file);

            appState.ServerFiles.Remove(file);
        }

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        // Delete subfolders by level

        foreach (var folder in appState.ServerFiles.ToList().Where(f => f.IsFolder && f.AbsolutePath.StartsWith(sfo.AbsolutePath)).OrderByDescending(o => o.Level))
        {
            await DeleteServerFolderAsync(appState, folder);

            appState.ServerFiles.Remove(folder);
        }
    }
    
    public static async ValueTask CopyFileAsync(AppState appState, LocalFileObject fo)
    {
        await CopyFileAsync(appState, fo.AbsolutePath, fo.AbsoluteServerPath, fo.LastWriteTime);
    }

    public static async ValueTask CopyFileAsync(AppState appState, string localFilePath, string serverFilePath, long fileTime = -1, long fileSizeBytes = -1)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        localFilePath = localFilePath.FormatLocalPath(appState);

        if (File.Exists(localFilePath) == false)
        {
            appState.Exceptions.Add($"File `{localFilePath}` does not exist");
            await appState.CancellationTokenSource.CancelAsync();
            return;
        }
     
        var spinnerText = appState.CurrentSpinner?.Text ?? string.Empty;

        if (spinnerText.IndexOf("...", StringComparison.Ordinal) > 0)
            spinnerText = spinnerText[..spinnerText.IndexOf("...", StringComparison.Ordinal)] + "...";
        
        serverFilePath = serverFilePath.FormatServerPath(appState);
        
        await EnsureServerPathExists(appState, serverFilePath.TrimEnd(serverFilePath.GetLastPathSegment()).TrimPath());

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        try
        {
            if (fileTime < 0 || fileSizeBytes < 0)
            {
                var fileInfo = new FileInfo(localFilePath);
                
                fileTime = fileInfo.LastWriteTime.ToFileTimeUtc();
                fileSizeBytes = fileInfo.Length;
            }

            var localFileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read);
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
            var fileExists = await ServerFileExistsAsync(appState, serverFilePath);
            
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = appState.FileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    var maxWriteSize = (int)appState.Client.MaxWriteSize;
                    var writeOffset = 0;
                        
                    while (localFileStream.Position < localFileStream.Length)
                    {
                        var buffer = new byte[maxWriteSize];
                        var bytesRead = localFileStream.Read(buffer, 0, buffer.Length);
                            
                        if (bytesRead < maxWriteSize)
                        {
                            Array.Resize(ref buffer, bytesRead);
                        }
                            
                        status = appState.FileStore.WriteFile(out _, handle, writeOffset, buffer);

                        if (status != NTStatus.STATUS_SUCCESS)
                        {
                            success = false;
                            break;
                        }

                        success = true;
                        writeOffset += bytesRead;

                        if (appState.CurrentSpinner is null)
                            continue;
                        
                        appState.CurrentSpinner.Text = $"{spinnerText} {localFilePath.GetLastPathSegment()} ({(writeOffset > 0 ? 100/(fileSizeBytes/writeOffset) : 0):N0}%)...";
                    }
                }

                if (handle is not null)
                    appState.FileStore.CloseFile(handle);

                if (success)
                    break;

                for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
                {
                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} Retry {attempt + 1} ({x:N0})...";

                    await Task.Delay(1000);
                }
            }

            if (success == false)
            {
                appState.Exceptions.Add($"Failed to copy file {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFilePath}`");
                await appState.CancellationTokenSource.CancelAsync();
                return;
            }
            
            await ChangeModifyDateAsync(appState, serverFilePath, fileTime);
        }
        catch
        {
            appState.Exceptions.Add($"Failed to copy file `{serverFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask ChangeModifyDateAsync(AppState appState, LocalFileObject fo)
{
    await ChangeModifyDateAsync(appState, fo.AbsoluteServerPath, fo.LastWriteTime);
}    

    public static async ValueTask ChangeModifyDateAsync(AppState appState, string serverFilePath, long fileTime)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        serverFilePath = serverFilePath.FormatServerPath(appState);
        
        var status = appState.FileStore.CreateFile(out var handle, out _, serverFilePath, (AccessMask)FileAccessMask.FILE_WRITE_ATTRIBUTES, 0, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, 0, null);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            var basicInfo = new FileBasicInformation
            {
                LastWriteTime = DateTime.FromFileTimeUtc(fileTime)
            };

            status = appState.FileStore.SetFileInformation(handle, basicInfo);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                appState.Exceptions.Add($"Failed to set last write time for file `{serverFilePath}`");
                await appState.CancellationTokenSource.CancelAsync();
            }
        }
        else
        {
            appState.Exceptions.Add($"Failed to prepare file for last write time set `{serverFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
        
        if (handle is not null)
            appState.FileStore.CloseFile(handle);
    }    

    #endregion
    
    #region Offline Support
    
    public static async ValueTask CreateOfflineFileAsync(AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var serverFilePath = $"{appState.Settings.Paths.RemoteRootPath}/app_offline.htm".FormatServerPath(appState);
        
        try
        {
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
            var fileExists = await ServerFileExistsAsync(appState, serverFilePath);
            var spinnerText = appState.CurrentSpinner?.Text ?? string.Empty;
            
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = appState.FileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    var numberOfBytesWritten = 0;
                    var data = Encoding.UTF8.GetBytes(appState.AppOfflineMarkup);

                    status = appState.FileStore.WriteFile(out numberOfBytesWritten, handle, 0, data);
                    
                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        throw new Exception("Failed to write to file");
                    }                    
                    
                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} ({numberOfBytesWritten.FormatBytes()})...";
                }

                if (handle is not null)
                    appState.FileStore.CloseFile(handle);

                if (success)
                    break;

                for (var x = appState.Settings.WriteRetryDelaySeconds; x >= 0; x--)
                {
                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} Retry {attempt + 1} ({x:N0})...";

                    await Task.Delay(1000);
                }
            }

            if (success == false)
            {
                appState.Exceptions.Add($"Failed to create offline file {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFilePath}`");
                await appState.CancellationTokenSource.CancelAsync();
            }
        }
        catch
        {
            appState.Exceptions.Add($"Failed to create offline file `{serverFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask DeleteOfflineFileAsync(AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var serverFilePath = $"{appState.Settings.Paths.RemoteRootPath}/app_offline.htm".FormatServerPath(appState);
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = appState.FileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = appState.FileStore.SetFileInformation(handle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
            }
            else
            {
                var fileExists = await ServerFileExistsAsync(appState, serverFilePath);
        
                if (fileExists == false)
                    success = true;
                else        
                    success = false;
            }

            if (handle is not null)
                appState.FileStore.CloseFile(handle);

            if (success)
                break;

            if (appState.CurrentSpinner is not null)
            {
                var text = appState.CurrentSpinner.Text;

                if (text.Contains("... Retry", StringComparison.Ordinal))
                    text = appState.CurrentSpinner.Text[..text.IndexOf("... Retry", StringComparison.Ordinal)] + "...";

                appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1} ({serverFilePath.GetLastPathSegment()})...";
            }

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete offline file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }
    
    #endregion
}