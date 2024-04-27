using System.Net.Sockets;
using SMBLibrary;
using SMBLibrary.Client;
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

    public static async Task CopyFolderAsync(AppState appState, string localSourcePath, string localDestinationPath, int maxTasks = 2)
    {
        if (maxTasks < 1)
            maxTasks = 2;

        // Get the subdirectories for the specified directory.
        var dir = new DirectoryInfo(localSourcePath);

        if (dir.Exists == false)
        {
            appState.Exceptions.Add($"Could find source folder (copy folder) `{localSourcePath}`");
            await appState.CancellationTokenSource.CancelAsync();
            return;
        }

        var dirs = dir.GetDirectories();

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
        
        foreach (var file in files)
        {
            var tempPath = Path.Combine(localDestinationPath, file.Name);

            try
            {
                file.CopyTo(tempPath, true);
            }
            catch
            {
                appState.Exceptions.Add($"Could not copy local file `{tempPath}`");
                await appState.CancellationTokenSource.CancelAsync();
                return;
            }

            if (appState.CancellationTokenSource.IsCancellationRequested)
                break;
        }

        var tasks = new List<Task>();
        var limit = 0;
        
        foreach (var subdir in dirs)
        {
            limit++;
            tasks.Add(CopyFolderAsync(appState, subdir.FullName, Path.Combine(localDestinationPath, subdir.Name), maxTasks));
            
            if (limit % maxTasks == 0)
            {
                if (appState.CancellationTokenSource.IsCancellationRequested == false)
                    await Task.WhenAll(tasks);
            }
            
            if (appState.CancellationTokenSource.IsCancellationRequested)
                break;
        }
        
        if (appState.CancellationTokenSource.IsCancellationRequested == false)
            await Task.WhenAll(tasks);
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
    
    public static SMB2Client? ConnectClient(AppState appState, bool verifyShare = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return null;
        
        var serverAvailable = false;
        
        using (var tcpClient = new TcpClient())
        {
            try
            {
                var result = tcpClient.BeginConnect(appState.Settings.ServerConnection.ServerAddress, 445, null, null);
                
                serverAvailable = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(appState.Settings.ServerConnection.ConnectTimeoutMs));
            }
            catch
            {
                serverAvailable = false;
            }
            finally
            {
                tcpClient.Close();
            }
        }

        if (serverAvailable == false)
        {
            appState.Exceptions.Add("Server is not responding");
            appState.CancellationTokenSource.Cancel();
            return null;
        }

        var client = new SMB2Client();
        var isConnected = client.Connect(appState.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, appState.Settings.ServerConnection.ResponseTimeoutMs);

        if (isConnected)
        {
            var status = client.Login(appState.Settings.ServerConnection.Domain, appState.Settings.ServerConnection.UserName, appState.Settings.ServerConnection.Password);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                if (verifyShare)
                {
                    var shares = client.ListShares(out status);

                    if (status == NTStatus.STATUS_SUCCESS)
                    {
                        if (shares.Contains(appState.Settings.ServerConnection.ShareName, StringComparer.OrdinalIgnoreCase) == false)
                        {
                            appState.Exceptions.Add("Network share not found on the server");
                            appState.CancellationTokenSource.Cancel();
                            return null;
                        }
                    }

                    else
                    {
                        appState.Exceptions.Add("Could not retrieve server shares list");
                        appState.CancellationTokenSource.Cancel();
                        return null;
                    }
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

        if (appState.CancellationTokenSource.IsCancellationRequested == false)
            return client;
        
        DisconnectClient(client);

        return null;
    }

    public static void DisconnectClient(SMB2Client? client)
    {
        if (client is null || client.IsConnected == false)
            return;

        try
        {
            client.Logoff();
        }

        finally
        {
            client.Disconnect();
        }
    }

    public static ISMBFileStore? GetFileStore(AppState appState, SMB2Client client)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return null;

        ISMBFileStore? fileStore = null;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            fileStore = client.TreeConnect(appState.Settings.ServerConnection.ShareName, out var status);

            if (status == NTStatus.STATUS_SUCCESS)
                break;

            Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (fileStore is not null)
            return fileStore;
        
        appState.Exceptions.Add($"Could not connect to the file share `{appState.Settings.ServerConnection.ShareName}`");
        appState.CancellationTokenSource.Cancel();

        return null;
    }
    
    #endregion
    
    #region Server Storage
    
    public static void RecurseServerPath(AppState appState, string path, bool includeHidden = false, int maxTasks = 8)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var client = ConnectClient(appState);
        
        if (client is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var fileStore = GetFileStore(appState, client);
        
        if (fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (maxTasks < 1)
            maxTasks = 8;

        try
        {
            #region Segment Files And Folders

            var files = new List<FileDirectoryInformation>();
            var directories = new List<FileDirectoryInformation>();
            var status = fileStore.CreateFile(out var fileFolderHandle, out _, path, AccessMask.GENERIC_READ,
                FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE, null);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                if (ServerFolderExists(appState, fileStore, path) == false)
                    return;
            }

            status = fileStore.QueryDirectory(out var fileFolderList, fileFolderHandle, "*",
                FileInformationClass.FileDirectoryInformation);

            if (fileFolderHandle is not null)
                status = fileStore.CloseFile(fileFolderHandle);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                foreach (var item in fileFolderList)
                {
                    if (appState.CancellationTokenSource.IsCancellationRequested)
                        break;

                    var file = (FileDirectoryInformation)item;

                    if (file.FileName is "." or "..")
                        continue;

                    if (includeHidden == false &&
                        (file.FileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                        continue;

                    if ((file.FileAttributes & FileAttributes.Directory) == FileAttributes.Directory)
                        directories.Add(file);
                    else
                        files.Add(file);
                }
            }

            else
            {
                appState.Exceptions.Add($"Cannot index server path `{path}`");
                appState.CancellationTokenSource.Cancel();
                return;
            }

            #endregion

            #region Files

            if (files.Count > 0)
            {
                Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = maxTasks }, (i, state) =>
                {
                    if (appState.CancellationTokenSource.IsCancellationRequested || state.ShouldExitCurrentIteration || state.IsStopped)
                        return;

                    var file = files[i];

                    try
                    {
                        var filePath = $"{path}\\{file.FileName}";

                        var fo = new ServerFileObject(appState, filePath.Trim('\\'), file.LastWriteTime.ToFileTimeUtc(),
                            file.EndOfFile, true, appState.Settings.Paths.RemoteRootPath);

                        if (FilePathShouldBeIgnoredDuringScan(appState, fo))
                            return;

                        appState.ServerFiles.Add(fo);
                    }
                    catch
                    {
                        appState.Exceptions.Add($"Could process server file `{file.FileName}`");
                        appState.CancellationTokenSource.Cancel();
                        state.Stop();
                    }
                });
            }

            #endregion

            #region Directories

            if (directories.Count == 0)
                return;

            Parallel.For(0, directories.Count, new ParallelOptions { MaxDegreeOfParallelism = maxTasks }, (i, state) =>
            {
                if (appState.CancellationTokenSource.IsCancellationRequested || state.ShouldExitCurrentIteration || state.IsStopped)
                    return;

                var directory = directories[i];

                try
                {
                    var directoryPath = $"{path}\\{directory.FileName}";

                    var fo = new ServerFileObject(appState, directoryPath.Trim('\\'),
                        directory.LastWriteTime.ToFileTimeUtc(), 0, false, appState.Settings.Paths.RemoteRootPath);

                    if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                        return;

                    if (appState.CurrentSpinner is not null && i % 3 == 0)
                        appState.CurrentSpinner.Text =
                            $"{appState.CurrentSpinner.Text[..appState.CurrentSpinner.Text.IndexOf("...", StringComparison.Ordinal)]}... {directory.FileName}/...";

                    appState.ServerFiles.Add(fo);

                    RecurseServerPath(appState, directoryPath, includeHidden, maxTasks);
                }
                catch
                {
                    appState.Exceptions.Add($"Could process server directory `{directory.FileName}`");
                    appState.CancellationTokenSource.Cancel();
                    state.Stop();
                }
            });

            #endregion
        }

        finally
        {
            DisconnectClient(client);
        }
    }
    
    public static bool ServerFileExists(AppState appState, ISMBFileStore? fileStore, string serverFilePath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        if (fileStore is null)
            return false;

        serverFilePath = serverFilePath.FormatServerPath(appState);
        
        var status = fileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (handle is not null)
            fileStore.CloseFile(handle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }
    
    public static bool ServerFolderExists(AppState appState, ISMBFileStore? fileStore, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        if (fileStore is null)
            return false;
        
        serverFolderPath = serverFolderPath.FormatServerPath(appState);
        
        var status = fileStore.CreateFile(out var handle, out _, serverFolderPath, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (handle is not null)
            fileStore.CloseFile(handle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    public static void EnsureServerPathExists(AppState appState, ISMBFileStore? fileStore, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (fileStore is null)
            return;

        serverFolderPath = serverFolderPath.FormatServerPath(appState);

        if (ServerFolderExists(appState, fileStore, serverFolderPath))
            return;

        var segments = serverFolderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;
        const string spinnerText = "Creating server folder...";

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (ServerFolderExists(appState, fileStore, buildingPath))
                continue;
            
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
                if (appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text = $"{spinnerText} `{buildingPath}`...";

                var status = fileStore.CreateFile(out var handle, out _, buildingPath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

                if (handle is not null)
                    fileStore.CloseFile(handle);

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

                    Thread.Sleep(1000);
                }
            }

            if (success)
                continue;
            
            appState.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{buildingPath}`");
            appState.CancellationTokenSource.Cancel();
            break;
        }
    }
    
    public static async ValueTask DeleteServerFileAsync(AppState appState, ISMBFileStore? fileStore, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (fileStore is null)
            return;

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = fileStore.CreateFile(out var handle, out _, sfo.AbsolutePath.TrimPath(), AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = fileStore.SetFileInformation(handle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
            }
            else
            {
                var fileExists = ServerFileExists(appState, fileStore, sfo.AbsolutePath.TrimPath());
        
                if (fileExists == false)
                    success = true;
                else        
                    success = false;
            }

            if (handle is not null)
                fileStore.CloseFile(handle);

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

    public static async ValueTask DeleteServerFolderAsync(AppState appState, ISMBFileStore? fileStore, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (fileStore is null)
            return;

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = fileStore.CreateFile(out var handle, out _, sfo.AbsolutePath.TrimPath(), AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = fileStore.SetFileInformation(handle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
            }
            else
            {
                var folderExists = ServerFolderExists(appState, fileStore, sfo.AbsolutePath.TrimPath());

                if (folderExists == false)
                    success = true;
                else                
                    success = false;
            }

            if (handle is not null)
                fileStore.CloseFile(handle);

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

    public static async ValueTask DeleteServerFolderRecursiveAsync(AppState appState, ISMBFileStore? fileStore, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (fileStore is null)
            return;

        var folderExists = ServerFolderExists(appState, fileStore, sfo.AbsolutePath.TrimPath());
        
        if (folderExists == false)
            return;
        
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        // Delete all files in the path

        var list = appState.ServerFiles.ToList();

        foreach (var file in list.ToList().Where(f => f.IsFile && f.AbsolutePath.StartsWith(sfo.AbsolutePath)))
        {
            await DeleteServerFileAsync(appState, fileStore, file);

            list.Remove(file);
        }

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        // Delete subfolders by level

        foreach (var folder in list.ToList().Where(f => f.IsFolder && f.AbsolutePath.StartsWith(sfo.AbsolutePath)).OrderByDescending(o => o.Level))
        {
            await DeleteServerFolderAsync(appState, fileStore, folder);

            list.Remove(folder);
        }
        
        appState.ServerFiles = new ConcurrentBag<ServerFileObject>(list);
    }
    
    public static async ValueTask CopyFileAsync(AppState appState, ISMBFileStore? fileStore, LocalFileObject fo)
    {
        await CopyFileAsync(appState, fileStore, fo.AbsolutePath, fo.AbsoluteServerPath, fo.LastWriteTime);
    }

    public static async ValueTask CopyFileAsync(AppState appState, ISMBFileStore? fileStore, string localFilePath, string serverFilePath, long fileTime = -1, long fileSizeBytes = -1)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (fileStore is null)
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
        
        EnsureServerPathExists(appState, fileStore, serverFilePath.TrimEnd(serverFilePath.GetLastPathSegment()).TrimPath());

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
            var fileExists = ServerFileExists(appState, fileStore, serverFilePath);
            
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = fileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    var maxWriteSize = (int)fileStore.MaxWriteSize;
                    var writeOffset = 0;
                        
                    while (localFileStream.Position < localFileStream.Length)
                    {
                        var buffer = new byte[maxWriteSize];
                        var bytesRead = localFileStream.Read(buffer, 0, buffer.Length);
                            
                        if (bytesRead < maxWriteSize)
                        {
                            Array.Resize(ref buffer, bytesRead);
                        }
                            
                        status = fileStore.WriteFile(out _, handle, writeOffset, buffer);

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
                    fileStore.CloseFile(handle);

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
            
            await ChangeModifyDateAsync(appState, fileStore, serverFilePath, fileTime);
        }
        catch
        {
            appState.Exceptions.Add($"Failed to copy file `{serverFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask ChangeModifyDateAsync(AppState appState, ISMBFileStore? fileStore, LocalFileObject fo)
    {
        await ChangeModifyDateAsync(appState, fileStore, fo.AbsoluteServerPath, fo.LastWriteTime);
    }    

    public static async ValueTask ChangeModifyDateAsync(AppState appState, ISMBFileStore? fileStore, string serverFilePath, long fileTime)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (fileStore is null)
            return;

        serverFilePath = serverFilePath.FormatServerPath(appState);
        
        var status = fileStore.CreateFile(out var handle, out _, serverFilePath, (AccessMask)FileAccessMask.FILE_WRITE_ATTRIBUTES, 0, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, 0, null);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            var basicInfo = new FileBasicInformation
            {
                LastWriteTime = DateTime.FromFileTimeUtc(fileTime)
            };

            status = fileStore.SetFileInformation(handle, basicInfo);

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
            fileStore.CloseFile(handle);
    }    

    #endregion
    
    #region Offline Support
    
    public static async ValueTask CreateOfflineFileAsync(AppState appState, ISMBFileStore? fileStore)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (fileStore is null)
            return;

        var serverFilePath = $"{appState.Settings.Paths.RemoteRootPath}/app_offline.htm".FormatServerPath(appState);
        
        try
        {
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
            var fileExists = ServerFileExists(appState, fileStore, serverFilePath);
            var spinnerText = appState.CurrentSpinner?.Text ?? string.Empty;
            
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = fileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                    
                if (status == NTStatus.STATUS_SUCCESS)
                {
                    var data = Encoding.UTF8.GetBytes(appState.AppOfflineMarkup);

                    status = fileStore.WriteFile(out var numberOfBytesWritten, handle, 0, data);

                    if (status != NTStatus.STATUS_SUCCESS)
                        success = false;
                    else
                        success = true;
                    
                    if (appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} ({numberOfBytesWritten.FormatBytes()})...";
                }

                if (handle is not null)
                    fileStore.CloseFile(handle);

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
    
    public static async ValueTask DeleteOfflineFileAsync(AppState appState, ISMBFileStore? fileStore)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (fileStore is null)
            return;

        var serverFilePath = $"{appState.Settings.Paths.RemoteRootPath}/app_offline.htm".FormatServerPath(appState);
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = fileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = fileStore.SetFileInformation(handle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
            }
            else
            {
                var fileExists = ServerFileExists(appState, fileStore, serverFilePath);
        
                if (fileExists == false)
                    success = true;
                else        
                    success = false;
            }

            if (handle is not null)
                fileStore.CloseFile(handle);

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