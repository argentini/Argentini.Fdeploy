using System.Net.Sockets;
using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Argentini.Fdeploy.Extensions;

public static class Smb
{
    public static async ValueTask<bool> ConnectAsync(this StorageRunner? storageRunner)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return false;
        
        var serverAvailable = false;
        
        using (var client = new TcpClient())
        {
            try
            {
                var result = client.BeginConnect(storageRunner.Settings.ServerConnection.ServerAddress, 445, null, null);
                
                serverAvailable = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(storageRunner.Settings.ServerConnection.ConnectTimeoutMs));
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
            storageRunner.Exceptions.Add("Server is not responding");
            await storageRunner.CancellationTokenSource.CancelAsync();
            return false;
        }
        
        var isConnected = storageRunner.Client.Connect(storageRunner.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, storageRunner.Settings.ServerConnection.ResponseTimeoutMs);
        var shares = new List<string>();

        if (isConnected)
        {
            var status = storageRunner.Client.Login(storageRunner.Settings.ServerConnection.Domain, storageRunner.Settings.ServerConnection.UserName, storageRunner.Settings.ServerConnection.Password);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                shares = storageRunner.Client.ListShares(out status);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    if (shares.Contains(storageRunner.Settings.ServerConnection.ShareName, StringComparer.OrdinalIgnoreCase) == false)
                    {
                        storageRunner.Exceptions.Add("Network share not found on the server");
                        await storageRunner.CancellationTokenSource.CancelAsync();
                    }
                }

                else
                {
                    storageRunner.Exceptions.Add("Could not retrieve server shares list");
                    await storageRunner.CancellationTokenSource.CancelAsync();
                }
            }

            else
            {
                storageRunner.Exceptions.Add("Server authentication failed");
                await storageRunner.CancellationTokenSource.CancelAsync();
            }
        }        

        else
        {
            storageRunner.Exceptions.Add("Could not connect to the server");
            await storageRunner.CancellationTokenSource.CancelAsync();
        }
        
        if (storageRunner.CancellationTokenSource.IsCancellationRequested)
            Disconnect(storageRunner);

        return false;
    }

    public static void Disconnect(this StorageRunner? storageRunner)
    {
        if (storageRunner?.Client.IsConnected == false)
            return;

        try
        {
            storageRunner?.Client.Logoff();
        }

        finally
        {
            storageRunner?.Client.Disconnect();
        }
    }
    
    public static async ValueTask<ISMBFileStore?> EnsureFileStoreAsync(this ISMBFileStore? fileStore, StorageRunner? storageRunner)
    {
        if (fileStore is not null)
            return fileStore;

        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return null;

        var retries = storageRunner.Settings.RetryCount > 0 ? storageRunner.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            fileStore = storageRunner.Client.TreeConnect(storageRunner.Settings.ServerConnection.ShareName, out var status);

            if (status == NTStatus.STATUS_SUCCESS)
                break;

            await Task.Delay(storageRunner.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (fileStore is not null)
            return fileStore;
        
        storageRunner.Exceptions.Add($"Could not connect to the file share `{storageRunner.Settings.ServerConnection.ShareName}`");
        await storageRunner.CancellationTokenSource.CancelAsync();

        return null;
    }

    public static async ValueTask RecurseSmbPathAsync(this ISMBFileStore? fileStore, string path, int level, List<FileObject> files, Spinner? spinner, string spinnerText, StorageRunner? storageRunner)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var status = NTStatus.STATUS_SUCCESS;
        
        if (fileStore is null)
            return;

        if (await FolderExistsAsync(path, storageRunner) == false)
            return;

        status = fileStore.CreateFile(out var directoryHandle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = fileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
            status = fileStore.CloseFile(directoryHandle);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                foreach (var item in fileList)
                {
                    if (storageRunner.CancellationTokenSource.IsCancellationRequested)
                        break;
                    
                    FileDirectoryInformation? file = null;

                    try
                    {
                        file = (FileDirectoryInformation)item;

                        if (file.FileName is "." or "..")
                            continue;

                        var filePath = $"{path}\\{file.FileName}";

                        if ((file.FileAttributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                            continue;
                        
                        var isDirectory = (file.FileAttributes & FileAttributes.Directory) == FileAttributes.Directory;

                        if (isDirectory)
                        {
                            if (storageRunner.Settings.Paths.RelativeIgnorePaths.Contains(filePath.NormalizePath().TrimStart(storageRunner.Settings.Paths.RemoteRootPath).TrimPath()) || storageRunner.Settings.Paths.IgnoreFoldersNamed.Contains(file.FileName))
                                continue;
                            
                            if (spinner is not null && level == 0)
                                spinner.Text = $"{spinnerText} {file.FileName}/...";
                            
                            await fileStore.RecurseSmbPathAsync(filePath, level + 1, files, spinner, spinnerText, storageRunner);
                        }

                        else
                        {
                            if (storageRunner.Settings.Paths.RelativeIgnoreFilePaths.Contains(filePath.NormalizePath().TrimStart(storageRunner.Settings.Paths.RemoteRootPath).TrimPath()) || storageRunner.Settings.Paths.IgnoreFilesNamed.Contains(file.FileName))
                                continue;

                            files.Add(new FileObject
                            {
                                FullPath = filePath.Trim('\\'),
                                FilePath = path.Trim('\\'),
                                FileName = file.FileName,
                                LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                                FileSizeBytes = file.Length
                            });
                        }
                    }
                    catch
                    {
                        storageRunner.Exceptions.Add($"Could process server file `{(file is null ? item.ToString() : file.FileName)}`");
                        await storageRunner.CancellationTokenSource.CancelAsync();
                    }
                }
            }
            else
            {
                storageRunner.Exceptions.Add($"Could not read the contents of server path `{path}`");
                await storageRunner.CancellationTokenSource.CancelAsync();
            }
        }
        else
        {
            storageRunner.Exceptions.Add($"Cannot write to server path `{path}`");
            await storageRunner.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask<bool> FileExistsAsync(this ISMBFileStore? fileStore, string filePath, StorageRunner? storageRunner)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return false;

        fileStore = await fileStore.EnsureFileStoreAsync(storageRunner);

        if (fileStore is null)
            return false;

        var status = fileStore.CreateFile(out var fileHandle, out _, filePath.NormalizeSmbPath(), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (fileHandle is not null)
            fileStore.CloseFile(fileHandle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }
    
    public static async ValueTask<bool> FolderExistsAsync(string destinationPath, StorageRunner? storageRunner)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return false;

        storageRunner.FileStore = await storageRunner.FileStore.EnsureFileStoreAsync(storageRunner);

        if (storageRunner.FileStore is null)
            return false;
        
        var status = storageRunner.FileStore.CreateFile(out var fileHandle, out _, destinationPath.NormalizeSmbPath(), AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (fileHandle is not null)
            storageRunner.FileStore.CloseFile(fileHandle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    public static async ValueTask EnsurePathExists(string destinationPath, StorageRunner? storageRunner, Spinner? spinner = null)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;
        
        storageRunner.FileStore = await storageRunner.FileStore.EnsureFileStoreAsync(storageRunner);

        if (storageRunner.FileStore is null)
            return;

        if (await FolderExistsAsync(destinationPath, storageRunner))
            return;

        var segments = destinationPath.NormalizeSmbPath().Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (await FolderExistsAsync(buildingPath, storageRunner))
                continue;
            
            var success = true;
            var retries = storageRunner.Settings.RetryCount > 0 ? storageRunner.Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = storageRunner.FileStore.CreateFile(out _, out _, $"{buildingPath}", AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    success = true;
                    break;
                }

                success = false;

                if (spinner is not null)
                {
                    var text = spinner.Text.TrimEnd($" Retry {attempt}...");
                    spinner.Text = $"{text} Retry {attempt + 1}...";
                }

                await Task.Delay(storageRunner.Settings.WriteRetryDelaySeconds * 1000);
            }

            if (success)
                continue;
            
            storageRunner.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{buildingPath}`");
            await storageRunner.CancellationTokenSource.CancelAsync();
            break;
        }
    }
    
    public static async ValueTask DeleteServerFileAsync(string serverFilePath, StorageRunner? storageRunner, Spinner? spinner = null)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;
        
        storageRunner.FileStore = await storageRunner.FileStore.EnsureFileStoreAsync(storageRunner);

        if (storageRunner.FileStore is null)
            return;

        var smbFilePath = serverFilePath.NormalizeSmbPath();
        var fileExists = await storageRunner.FileStore.FileExistsAsync(smbFilePath, storageRunner);
        
        if (fileExists == false)
            return;
        
        if (storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;

        var success = true;
        var retries = storageRunner.Settings.RetryCount > 0 ? storageRunner.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = storageRunner.FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = storageRunner.FileStore.SetFileInformation(fileHandle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
                status = storageRunner.FileStore.CloseFile(fileHandle);                
            }
            else
            {
                success = false;
            }

            if (success)
                break;

            if (spinner is not null)
            {
                var text = spinner.Text.TrimEnd($" Retry {attempt}...");
                spinner.Text = $"{text} Retry {attempt + 1}...";
            }

            await Task.Delay(storageRunner.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            storageRunner.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await storageRunner.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask CopyFileAsync(FileObject sourceFo, StorageRunner? storageRunner)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;

        var relativePathWithFile = sourceFo.FullPath.TrimPath().TrimStart(storageRunner.TrimmablePublishPath).TrimPath();
        
        await CopyFileAsync(relativePathWithFile, relativePathWithFile, storageRunner);
    }
    
    public static async ValueTask CopyFileAsync(string sourceFilePath, string destinationFilePath, StorageRunner? storageRunner, Spinner? spinner = null)
    {
        if (storageRunner is null || storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;
        
        storageRunner.FileStore = await storageRunner.FileStore.EnsureFileStoreAsync(storageRunner);

        if (storageRunner.FileStore is null)
            return;

        var smbFilePath = destinationFilePath.NormalizeSmbPath();
        var destinationPathWithoutFile = smbFilePath[..smbFilePath.LastIndexOf('\\')];

        await Smb.EnsurePathExists(destinationPathWithoutFile, storageRunner, spinner);

        if (storageRunner.CancellationTokenSource.IsCancellationRequested)
            return;

        var localFileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
        var success = true;
        var retries = storageRunner.Settings.RetryCount > 0 ? storageRunner.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var fileExists = await storageRunner.FileStore.FileExistsAsync(smbFilePath, storageRunner);
            
            var status = storageRunner.FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            
            if (status == NTStatus.STATUS_SUCCESS)
            {
                var writeOffset = 0;
                
                while (localFileStream.Position < localFileStream.Length)
                {
                    var buffer = new byte[(int)storageRunner.Client.MaxWriteSize];
                    var bytesRead = localFileStream.Read(buffer, 0, buffer.Length);
                    
                    if (bytesRead < (int)storageRunner.Client.MaxWriteSize)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }
                    
                    status = storageRunner.FileStore.WriteFile(out _, fileHandle, writeOffset, buffer);

                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        success = false;
                        break;
                    }

                    success = true;
                    writeOffset += bytesRead;
                }

                status = storageRunner.FileStore.CloseFile(fileHandle);
            }

            if (success)
                break;

            if (spinner is not null)
            {
                var text = spinner.Text.TrimEnd($" Retry {attempt}...");
                spinner.Text = $"{text} Retry {attempt + 1}...";
            }

            await Task.Delay(storageRunner.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            storageRunner.Exceptions.Add($"Failed to write file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await storageRunner.CancellationTokenSource.CancelAsync();
        }
    }
}