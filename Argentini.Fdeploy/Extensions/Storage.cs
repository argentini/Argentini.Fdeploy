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
        foreach (var subdir in Directory.GetDirectories(path).OrderBy(d => d))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                continue;
            
            var fo = new LocalFileObject(appState, subdir, directory.LastWriteTime.ToFileTimeUtc(), 0, false, appState.PublishPath);
            
            if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                continue;

            if (appState.CurrentSpinner is not null)
                appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.Text[..appState.CurrentSpinner.Text.IndexOf("...", StringComparison.Ordinal)]}... {fo.RelativeComparablePath}...";
            
            appState.LocalFiles.Add(fo);

            await RecurseLocalPathAsync(appState, subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path).OrderBy(f => f))
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

        return appState.Settings.Paths.IgnoreFilesNamed.Contains(fo.FileNameOrPathSegment);
    }
    
    #endregion
    
    #region SMB

    public static SMB2Client? CreateClient(AppState appState, bool verifyShare = false, bool suppressOutput = true)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return null;
        
        var serverAvailable = false;
        var client = new SMB2Client();
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        var spinnerText = appState.CurrentSpinner?.Text ?? string.Empty;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            using (var tcpClient = new TcpClient())
            {
                try
                {
                    if (suppressOutput == false && appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} Checking availability ({attempt + 1}/{retries})...";

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
                continue;

            if (suppressOutput == false && appState.CurrentSpinner is not null)
                appState.CurrentSpinner.Text = $"{spinnerText} Server online...";

            try
            {
                if (suppressOutput == false && appState.CurrentSpinner is not null)
                    appState.CurrentSpinner.Text = $"{spinnerText} Connecting ({attempt + 1}/{retries})...";

                var isConnected = client.Connect(appState.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, appState.Settings.ServerConnection.ResponseTimeoutMs);

                if (isConnected)
                {
                    if (suppressOutput == false && appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} Authenticating ({attempt + 1}/{retries})...";
                    
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
                                    if (suppressOutput == false && appState.CurrentSpinner is not null)
                                        appState.CurrentSpinner.Text = $"{spinnerText} Network share unavailable... Failed!";

                                    appState.Exceptions.Add("Network share not found on the server");
                                    appState.CancellationTokenSource.Cancel();
                                    DisconnectClient(client);
                                    return null;
                                }
                            }

                            else
                            {
                                if (suppressOutput == false && appState.CurrentSpinner is not null)
                                    appState.CurrentSpinner.Text = $"{spinnerText} Network shares unavailable... Failed!";

                                appState.Exceptions.Add("Could not retrieve server shares list");
                                appState.CancellationTokenSource.Cancel();
                                DisconnectClient(client);
                                return null;
                            }
                        }
                    }

                    else
                    {
                        if (suppressOutput == false && appState.CurrentSpinner is not null)
                            appState.CurrentSpinner.Text = $"{spinnerText} Authentication failed!";
                        
                        appState.Exceptions.Add("Server authentication failed");
                        appState.CancellationTokenSource.Cancel();
                    }
                }

                else
                {
                    if (suppressOutput == false && appState.CurrentSpinner is not null)
                        appState.CurrentSpinner.Text = $"{spinnerText} Connection failed!";

                    appState.Exceptions.Add("Could not connect to the server");
                    appState.CancellationTokenSource.Cancel();
                }

                if (appState.CancellationTokenSource.IsCancellationRequested == false)
                    return client;
                
                Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
            }
            catch
            {
                Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
            }
        }

        if (serverAvailable == false)
        {
            if (suppressOutput == false)
                appState.CurrentSpinner?.Fail($"{spinnerText} Could not connect to server... Failed!");

            appState.Exceptions.Add("Server is not responding");
            appState.CancellationTokenSource.Cancel();
        }

        else
        {
            if (suppressOutput == false)
                appState.CurrentSpinner?.Fail($"{spinnerText} Could not authenticate... Failed!");
        }

        appState.Exceptions.Add("Could not create SMB client");
        appState.CancellationTokenSource.Cancel();

        DisconnectClient(client);

        return null;
    }

    public static void ReConnectClient(this SMB2Client client, AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                var isConnected = client.Connect(appState.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, appState.Settings.ServerConnection.ResponseTimeoutMs);

                if (isConnected)
                {
                    var status = client.Login(appState.Settings.ServerConnection.Domain, appState.Settings.ServerConnection.UserName, appState.Settings.ServerConnection.Password);

                    if (status != NTStatus.STATUS_SUCCESS)
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
                    return;
                
                Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
            }
            catch
            {
                Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
            }
        }

        appState.Exceptions.Add("Could not create SMB client");
        appState.CancellationTokenSource.Cancel();

        DisconnectClient(client);
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
    
    public static ISMBFileStore? GetFileStore(this SMB2Client? client, AppState appState)
    {
        if (client is null || client.IsConnected == false || appState.CancellationTokenSource.IsCancellationRequested)
            return null;

        ISMBFileStore? fileStore = null;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            try
            {
                fileStore = client.TreeConnect(appState.Settings.ServerConnection.ShareName, out var status);

                if (status == NTStatus.STATUS_SUCCESS)
                    break;

                Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
            }
            catch
            {
                Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
            }
        }

        if (fileStore is not null)
            return fileStore;
        
        appState.Exceptions.Add($"Could not connect to the file share `{appState.Settings.ServerConnection.ShareName}`");
        appState.CancellationTokenSource.Cancel();

        return null;
    }
    
    public static void DisconnectFileStore(ISMBFileStore? fileStore)
    {
        if (fileStore is null)
            return;

        try
        {
            fileStore.Disconnect();
        }

        catch
        {
            // ignored
        }
    }
    
    #endregion
    
    #region Server Storage
    
    public static async Task RecurseServerPathAsync(AppState appState, string path, bool includeHidden = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        SMB2Client? client = null;
        ISMBFileStore? fileStore = null;
        
        try
        {
            client = CreateClient(appState);

            if (client is null || client.IsConnected == false || appState.CancellationTokenSource.IsCancellationRequested)
                return;

            fileStore = client.GetFileStore(appState);
        
            if (fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
                return;

            #region Segment Files And Folders

            var files = new List<FileDirectoryInformation>();
            var directories = new List<FileDirectoryInformation>();
            var status = fileStore.CreateFile(out var fileFolderHandle, out _, path, AccessMask.GENERIC_READ,
                FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE, null);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                if (client.ServerFolderExists(fileStore, appState, path) == false)
                    return;
            }

            status = fileStore.QueryDirectory(out var fileFolderList, fileFolderHandle, "*", FileInformationClass.FileDirectoryInformation);

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

                    if (includeHidden == false && (file.FileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden)
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
                await appState.CancellationTokenSource.CancelAsync();
                return;
            }

            #endregion

            #region Files

            if (files.Count != 0)
            {
                foreach (var file in files.OrderBy(f => f.FileName))
                {
                    try
                    {
                        if (appState.CancellationTokenSource.IsCancellationRequested)
                            return;

                        var filePath = $"{path}\\{file.FileName}";

                        var fo = new ServerFileObject(appState, filePath.Trim('\\'), file.LastWriteTime.ToFileTimeUtc(), file.EndOfFile, true, appState.Settings.ServerConnection.RemoteRootPath);

                        if (FilePathShouldBeIgnoredDuringScan(appState, fo))
                            return;

                        appState.ServerFiles.Add(fo);
                    }
                    catch
                    {
                        appState.Exceptions.Add($"Could not process server file `{file.FileName}`");
                        await appState.CancellationTokenSource.CancelAsync();
                    }
                }
            }

            #endregion

            #region Directories

            if (directories.Count == 0)
                return;

            var tasks = new List<Task>();
            var semaphore = new SemaphoreSlim(appState.Settings.MaxThreadCount);
            
            foreach (var directory in directories.OrderBy(f => f.FileName))
            {
                await semaphore.WaitAsync();

                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (appState.CancellationTokenSource.IsCancellationRequested)
                            return;

                        var directoryPath = $"{path}\\{directory.FileName}";
                        var fo = new ServerFileObject(appState, directoryPath.Trim('\\'), directory.LastWriteTime.ToFileTimeUtc(), 0, false, appState.Settings.ServerConnection.RemoteRootPath);

                        if (FolderPathShouldBeIgnoredDuringScan(appState, fo))
                            return;

                        if (appState.CurrentSpinner is not null)
                            appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.Text[..appState.CurrentSpinner.Text.IndexOf("...", StringComparison.Ordinal)]}... {fo.RelativeComparablePath}...";

                        appState.ServerFiles.Add(fo);

                        await RecurseServerPathAsync(appState, directoryPath, includeHidden);
                    }
                    catch
                    {
                        appState.Exceptions.Add($"Could not index server directory `{directory.FileName}`");
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
        finally
        {
            DisconnectFileStore(fileStore);
            DisconnectClient(client);
        }
    }
    
    public static bool ServerFileExists(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, string serverFilePath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        serverFilePath = serverFilePath.FormatServerPath(appState);
        
        var status = fileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (handle is not null)
            fileStore.CloseFile(handle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }
    
    public static bool ServerFolderExists(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return false;
        
        serverFolderPath = serverFolderPath.FormatServerPath(appState);
        
        var status = fileStore.CreateFile(out var handle, out _, serverFolderPath, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (handle is not null)
            fileStore.CloseFile(handle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    public static void EnsureServerPathExists(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        serverFolderPath = serverFolderPath.FormatServerPath(appState);

        if (client.ServerFolderExists(fileStore, appState, serverFolderPath))
            return;

        var segments = serverFolderPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;
        var spinnerText = $"Creating server path `{serverFolderPath}`...";

        if (appState.CurrentSpinner is not null)
            appState.CurrentSpinner.Text = spinnerText;

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (client.ServerFolderExists(fileStore, appState, buildingPath))
                continue;
            
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
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
                        appState.CurrentSpinner.Text = $"{spinnerText} Retry {attempt + 1} ({x:N0})...";

                    Thread.Sleep(1000);
                }
            }

            if (success)
                continue;
            
            appState.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFolderPath}`");
            appState.CancellationTokenSource.Cancel();
            break;
        }
    }
    
    public static void DeleteServerFile(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = fileStore.CreateFile(out var handle, out _, sfo is ServerFileObject serverFileObject ? serverFileObject.AbsolutePath : ((LocalFileObject)sfo).AbsoluteServerPath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

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
                var fileExists = client.ServerFileExists(fileStore, appState, sfo.AbsolutePath);
        
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

            Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{sfo.AbsolutePath}`");
            appState.CancellationTokenSource.Cancel();
        }
        else
        {
            sfo.IsDeleted = true;
        }
    }

    public static void DeleteServerFolder(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = fileStore.CreateFile(out var handle, out _, sfo.AbsolutePath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

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
                var folderExists = client.ServerFolderExists(fileStore, appState, sfo.AbsolutePath);

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

            Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{sfo.AbsolutePath}`");
            appState.CancellationTokenSource.Cancel();
        }
        else
        {
            sfo.IsDeleted = true;
        }
    }

    public static void DeleteServerFolderRecursive(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, FileObject sfo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var folderExists = client.ServerFolderExists(fileStore, appState, sfo.AbsolutePath);
        
        if (folderExists == false)
            return;
        
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        // Delete all files in the path

        foreach (var file in appState.ServerFiles.ToList().Where(f => f is { IsFile: true, IsDeleted: false } && f.AbsolutePath.StartsWith(sfo.AbsolutePath)))
        {
            client.DeleteServerFile(fileStore, appState, file);
        }

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        // Delete subfolders by level

        foreach (var folder in appState.ServerFiles.ToList().Where(f => f is { IsFolder: true, IsDeleted: false } && f.AbsolutePath.StartsWith(sfo.AbsolutePath)).OrderByDescending(o => o.Level))
        {
            client.DeleteServerFolder(fileStore, appState, folder);
        }
    }
    
    public static void CopyFile(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, LocalFileObject fo)
    {
        client.CopyFile(fileStore, appState, fo.AbsolutePath, fo.AbsoluteServerPath, fo.LastWriteTime);
    }

    public static void CopyFile(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, string localFilePath, string serverFilePath, long fileTime = -1, long fileSizeBytes = -1)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        try
        {
            if (client is null)
            {
                client = CreateClient(appState);
                fileStore = client.GetFileStore(appState);
            }
        
            else if (client.IsConnected == false)
            {
                client.ReConnectClient(appState);
                fileStore = client.GetFileStore(appState);
            }

            fileStore ??= client.GetFileStore(appState);

            if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            {
                appState.Exceptions.Add("Could not reconnect the SMB client during file copy");
                appState.CancellationTokenSource.Cancel();
                return;
            }

            localFilePath = localFilePath.FormatLocalPath(appState);
            
            if (File.Exists(localFilePath) == false)
            {
                appState.Exceptions.Add($"File `{localFilePath}` does not exist");
                appState.CancellationTokenSource.Cancel();
                return;
            }
         
            var spinnerText = appState.CurrentSpinner?.Text ?? string.Empty;
            
            if (spinnerText.IndexOf("...", StringComparison.Ordinal) > 0)
                spinnerText = spinnerText[..spinnerText.IndexOf("...", StringComparison.Ordinal)] + "...";            

            if (appState.CurrentSpinner is not null)
                appState.CurrentSpinner.Text = $"{spinnerText} {localFilePath.TrimPath().TrimStart(appState.TrimmablePublishPath).TrimPath()} (0%)...";
            
            serverFilePath = serverFilePath.FormatServerPath(appState);

            client.EnsureServerPathExists(fileStore, appState, serverFilePath.TrimEnd(serverFilePath.GetLastPathSegment()).TrimPath());
            
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

                var success = true;
                var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;

                using (var localFileStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read))
                {
                    var fileExists = client.ServerFileExists(fileStore, appState, serverFilePath);
                    
                    for (var attempt = 0; attempt < retries; attempt++)
                    {
                        var status = fileStore.CreateFile(out var handle, out _, serverFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
                            
                        if (status == NTStatus.STATUS_SUCCESS)
                        {
                            const int maxWriteSize = 1048576; // 1mb
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
                                
                                appState.CurrentSpinner.Text = $"{spinnerText} {localFilePath.TrimPath().TrimStart(appState.TrimmablePublishPath).TrimPath()} ({(writeOffset > 0 ? 100/(fileSizeBytes/writeOffset) : 0):N0}%)...";
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

                            Thread.Sleep(1000);
                        }
                    }
                }

                if (success == false)
                {
                    appState.Exceptions.Add($"Failed to copy file {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFilePath}`");
                    appState.CancellationTokenSource.Cancel();
                    return;
                }
                
                client.ChangeModifyDate(fileStore, appState, serverFilePath, fileTime);
            }
            catch
            {
                appState.Exceptions.Add($"Failed to copy file `{serverFilePath}`");
                appState.CancellationTokenSource.Cancel();
            }
        }
        catch
        {
            appState.Exceptions.Add($"Failed to copy file `{serverFilePath}`");
            appState.CancellationTokenSource.Cancel();
        }
    }
    
    public static void ChangeModifyDate(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, LocalFileObject fo)
    {
        client.ChangeModifyDate(fileStore, appState, fo.AbsoluteServerPath, fo.LastWriteTime);
    }    

    public static void ChangeModifyDate(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState, string serverFilePath, long fileTime)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
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
                appState.CancellationTokenSource.Cancel();
            }
        }
        else
        {
            appState.Exceptions.Add($"Failed to prepare file for last write time set `{serverFilePath}`");
            appState.CancellationTokenSource.Cancel();
        }
        
        if (handle is not null)
            fileStore.CloseFile(handle);
    }    

    #endregion
    
    #region Offline Support
    
    public static void TakeServerOffline(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var serverFilePath = $"{appState.Settings.ServerConnection.RemoteRootPath}\\app_offline.htm";
        
        try
        {
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
            var fileExists = client.ServerFileExists(fileStore, appState, serverFilePath);
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

                    Thread.Sleep(1000);
                }
            }

            if (success)
                return;
            
            appState.Exceptions.Add($"Failed to create offline file {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFilePath}`");
            appState.CancellationTokenSource.Cancel();
        }
        catch
        {
            appState.Exceptions.Add($"Failed to create offline file `{serverFilePath}`");
            appState.CancellationTokenSource.Cancel();
        }
    }
    
    public static void BringServerOnline(this SMB2Client? client, ISMBFileStore? fileStore, AppState appState)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (client is null)
        {
            client = CreateClient(appState);
            fileStore = client.GetFileStore(appState);
        }
        
        else if (client.IsConnected == false)
        {
            client.ReConnectClient(appState);
            fileStore = client.GetFileStore(appState);
        }

        fileStore ??= client.GetFileStore(appState);

        if (client is null || client.IsConnected == false || fileStore is null || appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var serverFilePath = $"{appState.Settings.ServerConnection.RemoteRootPath}/app_offline.htm".FormatServerPath(appState);
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
                var fileExists = client.ServerFileExists(fileStore, appState, serverFilePath);
        
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

            Thread.Sleep(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success)
            return;
        
        appState.Exceptions.Add($"Failed to delete offline file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{serverFilePath}`");
        appState.CancellationTokenSource.Cancel();
    }
    
    #endregion
}