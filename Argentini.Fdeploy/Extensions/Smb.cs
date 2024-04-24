using System.Net.Sockets;
using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Argentini.Fdeploy.Extensions;

public sealed class SmbConfig
{
    public SMB2Client Client { get; set; } = new();
    public ISMBFileStore? FileStore { get; set; }
    public Settings Settings { get; set; } = new();
    public List<string> Exceptions { get; set; } = [];
    public CancellationTokenSource CancellationTokenSource { get; set; } = new();
    public Spinner? Spinner { get; set; }
    public List<FileObject> Files { get; set; } = [];
}

public static class Smb
{
    public static void Connect(SmbConfig smbConfig)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var serverAvailable = false;
        
        using (var client = new TcpClient())
        {
            try
            {
                var result = client.BeginConnect(smbConfig.Settings.ServerConnection.ServerAddress, 445, null, null);
                
                serverAvailable = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(smbConfig.Settings.ServerConnection.ConnectTimeoutMs));
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
            smbConfig.Exceptions.Add("Server is not responding");
            smbConfig.CancellationTokenSource.Cancel();
            return;
        }
        
        var isConnected = smbConfig.Client.Connect(smbConfig.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, smbConfig.Settings.ServerConnection.ResponseTimeoutMs);
        var shares = new List<string>();

        if (isConnected)
        {
            var status = smbConfig.Client.Login(smbConfig.Settings.ServerConnection.Domain, smbConfig.Settings.ServerConnection.UserName, smbConfig.Settings.ServerConnection.Password);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                shares = smbConfig.Client.ListShares(out status);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    if (shares.Contains(smbConfig.Settings.ServerConnection.ShareName, StringComparer.OrdinalIgnoreCase) == false)
                    {
                        smbConfig.Exceptions.Add("Network share not found on the server");
                        smbConfig.CancellationTokenSource.Cancel();
                    }

                    else
                    {
                        EstablishFileStore(smbConfig);
                    }
                }

                else
                {
                    smbConfig.Exceptions.Add("Could not retrieve server shares list");
                    smbConfig.CancellationTokenSource.Cancel();
                }
            }

            else
            {
                smbConfig.Exceptions.Add("Server authentication failed");
                smbConfig.CancellationTokenSource.Cancel();
            }
        }        

        else
        {
            smbConfig.Exceptions.Add("Could not connect to the server");
            smbConfig.CancellationTokenSource.Cancel();
        }
        
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            Disconnect(smbConfig);
    }

    public static void Disconnect(SmbConfig smbConfig)
    {
        if (smbConfig.Client.IsConnected == false)
            return;

        try
        {
            smbConfig.Client.Logoff();
        }

        finally
        {
            smbConfig.Client.Disconnect();
        }
    }

    public static void EstablishFileStore(SmbConfig smbConfig)
    {
        var retries = smbConfig.Settings.RetryCount > 0 ? smbConfig.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            smbConfig.FileStore = smbConfig.Client.TreeConnect(smbConfig.Settings.ServerConnection.ShareName, out var status);

            if (status == NTStatus.STATUS_SUCCESS)
                break;

            Task.Delay(smbConfig.Settings.WriteRetryDelaySeconds * 1000).GetAwaiter();
        }

        if (smbConfig.FileStore is not null)
            return;
        
        smbConfig.Exceptions.Add($"Could not connect to the file share `{smbConfig.Settings.ServerConnection.ShareName}`");
        smbConfig.CancellationTokenSource.Cancel();
    }
    
    public static async ValueTask EnsureFileStoreAsync(SmbConfig smbConfig)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        var retries = smbConfig.Settings.RetryCount > 0 ? smbConfig.Settings.RetryCount : 1;

        for (var attempt = 0; attempt < retries; attempt++)
        {
            smbConfig.FileStore = smbConfig.Client.TreeConnect(smbConfig.Settings.ServerConnection.ShareName, out var status);

            if (status == NTStatus.STATUS_SUCCESS)
                break;

            await Task.Delay(smbConfig.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (smbConfig.FileStore is not null)
            return;
        
        smbConfig.Exceptions.Add($"Could not connect to the file share `{smbConfig.Settings.ServerConnection.ShareName}`");
        await smbConfig.CancellationTokenSource.CancelAsync();
    }

    public static async ValueTask RecurseSmbPathAsync(SmbConfig smbConfig, string path, int level)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        var status = NTStatus.STATUS_SUCCESS;
        
        if (smbConfig.FileStore is null)
            return;

        status = smbConfig.FileStore.CreateFile(out var directoryHandle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status != NTStatus.STATUS_SUCCESS)
        {
            if (await FolderExistsAsync(smbConfig, path) == false)
                return;
        }

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = smbConfig.FileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
            status = smbConfig.FileStore.CloseFile(directoryHandle);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                foreach (var item in fileList)
                {
                    if (smbConfig.CancellationTokenSource.IsCancellationRequested)
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
                            if (smbConfig.Settings.Paths.RelativeIgnorePaths.Contains(filePath.NormalizePath().TrimStart(smbConfig.Settings.Paths.RemoteRootPath).TrimPath()) || smbConfig.Settings.Paths.IgnoreFoldersNamed.Contains(file.FileName))
                                continue;
                            
                            if (smbConfig.Spinner is not null && level == 0)
                                smbConfig.Spinner.Text = $"{smbConfig.Spinner.Text[..smbConfig.Spinner.Text.IndexOf("...", StringComparison.Ordinal)]}... {file.FileName}/...";
                            
                            await RecurseSmbPathAsync(smbConfig, filePath, level + 1);
                        }

                        else
                        {
                            if (smbConfig.Settings.Paths.RelativeIgnoreFilePaths.Contains(filePath.NormalizePath().TrimStart(smbConfig.Settings.Paths.RemoteRootPath).TrimPath()) || smbConfig.Settings.Paths.IgnoreFilesNamed.Contains(file.FileName))
                                continue;

                            smbConfig.Files.Add(new FileObject
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
                        smbConfig.Exceptions.Add($"Could process server file `{(file is null ? item.ToString() : file.FileName)}`");
                        await smbConfig.CancellationTokenSource.CancelAsync();
                    }
                }
            }
            else
            {
                smbConfig.Exceptions.Add($"Could not read the contents of server path `{path}`");
                await smbConfig.CancellationTokenSource.CancelAsync();
            }
        }
        else
        {
            smbConfig.Exceptions.Add($"Cannot write to server path `{path}`");
            await smbConfig.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask<bool> FileExistsAsync(SmbConfig smbConfig, string filePath)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return false;

        await EnsureFileStoreAsync(smbConfig);

        if (smbConfig.FileStore is null)
            return false;

        var status = smbConfig.FileStore.CreateFile(out var fileHandle, out _, filePath.NormalizeSmbPath(), AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (fileHandle is not null)
            smbConfig.FileStore.CloseFile(fileHandle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }
    
    public static async ValueTask<bool> FolderExistsAsync(SmbConfig smbConfig, string destinationPath)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return false;

        await EnsureFileStoreAsync(smbConfig);

        if (smbConfig.FileStore is null)
            return false;
        
        var status = smbConfig.FileStore.CreateFile(out var fileHandle, out _, destinationPath.NormalizeSmbPath(), AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (fileHandle is not null)
            smbConfig.FileStore.CloseFile(fileHandle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    public static async ValueTask EnsurePathExists(SmbConfig smbConfig, string destinationPath)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(smbConfig);

        if (smbConfig.FileStore is null)
            return;

        if (await FolderExistsAsync(smbConfig, destinationPath))
            return;

        var segments = destinationPath.NormalizeSmbPath().Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (await FolderExistsAsync(smbConfig, buildingPath))
                continue;
            
            var success = true;
            var retries = smbConfig.Settings.RetryCount > 0 ? smbConfig.Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = smbConfig.FileStore.CreateFile(out _, out _, $"{buildingPath}", AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    success = true;
                    break;
                }

                success = false;

                if (smbConfig.Spinner is not null)
                {
                    var text = smbConfig.Spinner.Text.TrimEnd($" Retry {attempt}...");
                    smbConfig.Spinner.Text = $"{text} Retry {attempt + 1}...";
                }

                await Task.Delay(smbConfig.Settings.WriteRetryDelaySeconds * 1000);
            }

            if (success)
                continue;
            
            smbConfig.Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{buildingPath}`");
            await smbConfig.CancellationTokenSource.CancelAsync();
            break;
        }
    }
    
    public static async ValueTask DeleteServerFileAsync(SmbConfig smbConfig, string serverFilePath)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(smbConfig);

        if (smbConfig.FileStore is null)
            return;

        var smbFilePath = serverFilePath.NormalizeSmbPath();
        var fileExists = await FileExistsAsync(smbConfig, smbFilePath);
        
        if (fileExists == false)
            return;
        
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        var success = true;
        var retries = smbConfig.Settings.RetryCount > 0 ? smbConfig.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var status = smbConfig.FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.DELETE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                var fileDispositionInformation = new FileDispositionInformation
                {
                    DeletePending = true
                };
                
                status = smbConfig.FileStore.SetFileInformation(fileHandle, fileDispositionInformation);
                success = status == NTStatus.STATUS_SUCCESS;
                status = smbConfig.FileStore.CloseFile(fileHandle);                
            }
            else
            {
                success = false;
            }

            if (success)
                break;

            if (smbConfig.Spinner is not null)
            {
                var text = smbConfig.Spinner.Text.TrimEnd($" Retry {attempt}...");
                smbConfig.Spinner.Text = $"{text} Retry {attempt + 1}...";
            }

            await Task.Delay(smbConfig.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            smbConfig.Exceptions.Add($"Failed to delete file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await smbConfig.CancellationTokenSource.CancelAsync();
        }
    }
    
    public static async ValueTask CopyFileAsync(SmbConfig smbConfig, string trimmablePublishPath, FileObject sourceFo)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        var relativePathWithFile = sourceFo.FullPath.TrimPath().TrimStart(trimmablePublishPath).TrimPath();
        
        await CopyFileAsync(smbConfig, relativePathWithFile, relativePathWithFile);
    }
    
    public static async ValueTask CopyFileAsync(SmbConfig smbConfig, string sourceFilePath, string destinationFilePath)
    {
        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        await EnsureFileStoreAsync(smbConfig);

        if (smbConfig.FileStore is null)
            return;

        var smbFilePath = destinationFilePath.NormalizeSmbPath();
        var destinationPathWithoutFile = smbFilePath[..smbFilePath.LastIndexOf('\\')];

        await EnsurePathExists(smbConfig, destinationPathWithoutFile);

        if (smbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        var localFileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
        var success = true;
        var retries = smbConfig.Settings.RetryCount > 0 ? smbConfig.Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var fileExists = await FileExistsAsync(smbConfig, smbFilePath);
            var status = smbConfig.FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            
            if (status == NTStatus.STATUS_SUCCESS)
            {
                var writeOffset = 0;
                
                while (localFileStream.Position < localFileStream.Length)
                {
                    var buffer = new byte[(int)smbConfig.Client.MaxWriteSize];
                    var bytesRead = localFileStream.Read(buffer, 0, buffer.Length);
                    
                    if (bytesRead < (int)smbConfig.Client.MaxWriteSize)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }
                    
                    status = smbConfig.FileStore.WriteFile(out _, fileHandle, writeOffset, buffer);

                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        success = false;
                        break;
                    }

                    success = true;
                    writeOffset += bytesRead;
                }

                status = smbConfig.FileStore.CloseFile(fileHandle);
            }

            if (success)
                break;

            if (smbConfig.Spinner is not null)
            {
                var text = smbConfig.Spinner.Text.TrimEnd($" Retry {attempt}...");
                smbConfig.Spinner.Text = $"{text} Retry {attempt + 1}...";
            }

            await Task.Delay(smbConfig.Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            smbConfig.Exceptions.Add($"Failed to write file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await smbConfig.CancellationTokenSource.CancelAsync();
        }
    }
}