using System.Net.Sockets;
using SMBLibrary;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Argentini.Fdeploy.Extensions;

public static class Storage
{
    public static async ValueTask RecurseLocalPathAsync(AppState appState, string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                continue;
            
            var relativeComparablePath = subdir.NormalizePath().TrimStart(appState.PublishPath.TrimPath()).TrimPath();
            var segmentName = subdir.GetLastPathSegment().TrimPath();
            
            if (FolderPathShouldBeIgnoredDuringScan(appState, relativeComparablePath, segmentName))
                continue;

            appState.LocalFiles.Add(new FileObject
            {
                FullPath = subdir,
                FilePath = subdir.TrimPath().TrimEnd(subdir.GetLastPathSegment()).TrimPath(),
                FileName = segmentName,
                LastWriteTime = directory.LastWriteTime.ToFileTimeUtc(),
                FileSizeBytes = 0,
                RelativeComparablePath = relativeComparablePath,
                IsFolder = true
            });

            await RecurseLocalPathAsync(appState, subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path))
        {
            try
            {
                var file = new FileInfo(filePath);

                if ((file.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                    continue;

                var relativeComparablePath = filePath.NormalizePath().TrimStart(appState.PublishPath.TrimPath()).TrimPath();
            
                if (FilePathShouldBeIgnoredDuringScan(appState, relativeComparablePath, file.Name))
                    continue;
                
                appState.LocalFiles.Add(new FileObject
                {
                    FullPath = filePath,
                    FilePath = file.DirectoryName.TrimPath(),
                    FileName = file.Name,
                    LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                    FileSizeBytes = file.Length,
                    RelativeComparablePath = relativeComparablePath,
                    IsFile = true
                });
            }
            catch
            {
                appState.Exceptions.Add($"Could process local file `{filePath}`");
                await appState.CancellationTokenSource.CancelAsync();
            }
        }
    }

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

    public static bool FolderPathShouldBeIgnoredDuringScan(AppState appState, string path, string segmentName)
    {
        foreach (var ignorePath in appState.Settings.Paths.RelativeIgnorePaths)
        {
            if (path != ignorePath)
                continue;

            return true;
        }

        return appState.Settings.Paths.IgnoreFoldersNamed.Contains(segmentName);
    }

    public static bool FilePathShouldBeIgnoredDuringScan(AppState appState, string path, string segmentName)
    {
        foreach (var ignorePath in appState.Settings.Paths.RelativeIgnoreFilePaths)
        {
            if (path != ignorePath)
                continue;

            return true;
        }

        return appState.Settings.Paths.IgnoreFilesNamed.Contains(segmentName);
    }

    public static async ValueTask RecurseSmbPathAsync(AppState appState, string path, bool includeHidden = false)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var status = NTStatus.STATUS_SUCCESS;
        
        if (appState.FileStore is null)
            return;

        status = appState.FileStore.CreateFile(out var directoryHandle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            if (await FolderExistsAsync(appState, path) == false)
                return;
        }

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = appState.FileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
            status = appState.FileStore.CloseFile(directoryHandle);

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
                        var relativeComparablePath = filePath.TrimStart(appState.Settings.Paths.RemoteRootPath.NormalizeSmbPath()).TrimPath().SetNativePathSeparators();

                        if (includeHidden == false && (file.FileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                            continue;
                        
                        var isDirectory = (file.FileAttributes & FileAttributes.Directory) == FileAttributes.Directory;
                        
                        if (isDirectory)
                        {
                            if (FolderPathShouldBeIgnoredDuringScan(appState, relativeComparablePath, file.FileName))
                                continue;
                            
                            if (appState.CurrentSpinner is not null)
                                appState.CurrentSpinner.Text = $"{appState.CurrentSpinner.Text[..appState.CurrentSpinner.Text.IndexOf("...", StringComparison.Ordinal)]}... {file.FileName}/...";
                            
                            appState.ServerFiles.Add(new FileObject
                            {
                                FullPath = filePath.Trim('\\'),
                                FilePath = path.Trim('\\'),
                                FileName = file.FileName,
                                LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                                FileSizeBytes = 0,
                                RelativeComparablePath = relativeComparablePath,
                                IsFolder = true
                            });
                            
                            await RecurseSmbPathAsync(appState, filePath);
                        }

                        else
                        {
                            if (FilePathShouldBeIgnoredDuringScan(appState, relativeComparablePath, file.FileName))
                                continue;

                            appState.ServerFiles.Add(new FileObject
                            {
                                FullPath = filePath.Trim('\\'),
                                FilePath = path.Trim('\\'),
                                FileName = file.FileName,
                                LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                                FileSizeBytes = file.Length,
                                RelativeComparablePath = relativeComparablePath,
                                IsFile = true
                            });
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
    
    public static async ValueTask<bool> FileExistsAsync(AppState appState, string filePath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return false;

        var status = appState.FileStore.CreateFile(out var fileHandle, out _, filePath.NormalizeSmbPath(), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (fileHandle is not null)
            appState.FileStore.CloseFile(fileHandle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }
    
    public static async ValueTask<bool> FolderExistsAsync(AppState appState, string destinationPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return false;

        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return false;
        
        var status = appState.FileStore.CreateFile(out var fileHandle, out _, destinationPath.NormalizeSmbPath(), AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (fileHandle is not null)
            appState.FileStore.CloseFile(fileHandle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    public static async ValueTask EnsurePathExists(AppState appState, string destinationPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        if (await FolderExistsAsync(appState, destinationPath))
            return;

        var segments = destinationPath.NormalizeSmbPath().Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (await FolderExistsAsync(appState, buildingPath))
                continue;
            
            var success = true;
            var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = appState.FileStore.CreateFile(out _, out _, $"{buildingPath}", AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    success = true;
                    break;
                }

                success = false;

                if (appState.CurrentSpinner is not null)
                {
                    var text = appState.CurrentSpinner.Text.TrimEnd($" Retry {attempt}...");
                    appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1}...";
                }

                await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
            }

            if (success)
                continue;
            
            appState.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{buildingPath}`");
            await appState.CancellationTokenSource.CancelAsync();
            break;
        }
    }
    
    public static async ValueTask CopyFileAsync(AppState appState, string trimmablePublishPath, FileObject sourceFo)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var relativePathWithFile = sourceFo.FullPath.TrimPath().TrimStart(trimmablePublishPath).TrimPath();
        
        await CopyFileAsync(appState, relativePathWithFile, relativePathWithFile);
    }
    
    public static async ValueTask CopyFileAsync(AppState appState, string sourceFilePath, string destinationFilePath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var smbFilePath = destinationFilePath.NormalizeSmbPath();
        var destinationPathWithoutFile = smbFilePath[..smbFilePath.LastIndexOf('\\')];

        await EnsurePathExists(appState, destinationPathWithoutFile);

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        var localFileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var fileExists = await FileExistsAsync(appState, smbFilePath);
            var status = appState.FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            
            if (status == NTStatus.STATUS_SUCCESS)
            {
                var writeOffset = 0;
                
                while (localFileStream.Position < localFileStream.Length)
                {
                    var buffer = new byte[(int)appState.Client.MaxWriteSize];
                    var bytesRead = localFileStream.Read(buffer, 0, buffer.Length);
                    
                    if (bytesRead < (int)appState.Client.MaxWriteSize)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }
                    
                    status = appState.FileStore.WriteFile(out _, fileHandle, writeOffset, buffer);

                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        success = false;
                        break;
                    }

                    success = true;
                    writeOffset += bytesRead;
                }

                status = appState.FileStore.CloseFile(fileHandle);
            }

            if (success)
                break;

            if (appState.CurrentSpinner is not null)
            {
                var text = appState.CurrentSpinner.Text.TrimEnd($" Retry {attempt}...");
                appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1}...";
            }

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to write file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask DeleteServerFileAsync(AppState appState, string serverFilePath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var smbFilePath = serverFilePath.NormalizeSmbPath();
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = appState.FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = appState.FileStore.SetFileInformation(fileHandle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
                status = appState.FileStore.CloseFile(fileHandle);                
            }
            else
            {
                var fileExists = await FileExistsAsync(appState, smbFilePath);
        
                if (fileExists == false)
                    success = true;
                else        
                    success = false;
            }

            if (success)
                break;

            if (appState.CurrentSpinner is not null)
            {
                var text = appState.CurrentSpinner.Text;

                if (text.Contains("... Retry", StringComparison.Ordinal))
                    text = appState.CurrentSpinner.Text[..text.IndexOf("... Retry", StringComparison.Ordinal)] + "...";

                appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1} ({smbFilePath.GetLastPathSegment()})...";
            }

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }

    public static async ValueTask DeleteServerFolderAsync(AppState appState, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var smbFolderPath = serverFolderPath.NormalizeSmbPath();
        var success = true;
        var retries = appState.Settings.RetryCount > 0 ? appState.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = appState.FileStore.CreateFile(out var fileHandle, out _, smbFolderPath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = appState.FileStore.SetFileInformation(fileHandle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
                status = appState.FileStore.CloseFile(fileHandle);
            }
            else
            {
                var folderExists = await FolderExistsAsync(appState, smbFolderPath);

                if (folderExists == false)
                    success = true;
                else                
                    success = false;
            }

            if (success)
                break;

            if (appState.CurrentSpinner is not null)
            {
                var text = appState.CurrentSpinner.Text;

                if (text.Contains("... Retry", StringComparison.Ordinal))
                    text = appState.CurrentSpinner.Text[..text.IndexOf("... Retry", StringComparison.Ordinal)] + "...";
                
                appState.CurrentSpinner.Text = $"{text} Retry {attempt + 1} ({smbFolderPath.GetLastPathSegment()}/)...";
            }

            await Task.Delay(appState.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            appState.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFolderPath}`");
            await appState.CancellationTokenSource.CancelAsync();
        }
    }

    public static async ValueTask DeleteServerFolderRecursiveAsync(AppState appState, string serverFolderPath)
    {
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(appState);

        if (appState.FileStore is null)
            return;

        var smbFolderPath = serverFolderPath.NormalizeSmbPath();
        var folderExists = await FolderExistsAsync(appState, smbFolderPath);
        
        if (folderExists == false)
            return;
        
        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;

        // Delete all files in the path

        foreach (var file in appState.ServerFiles.ToList().Where(f => f.IsFile && f.FullPath.StartsWith(smbFolderPath)))
        {
            await DeleteServerFileAsync(appState, file.FullPath);

            appState.ServerFiles.Remove(file);
        }

        if (appState.CancellationTokenSource.IsCancellationRequested)
            return;
        
        // Delete subfolders by level

        foreach (var folder in appState.ServerFiles.ToList().Where(f => f.IsFolder && f.FullPath.StartsWith(smbFolderPath)).OrderByDescending(o => o.Level))
        {
            await DeleteServerFolderAsync(appState, folder.FullPath);

            appState.ServerFiles.Remove(folder);
        }
    }
}