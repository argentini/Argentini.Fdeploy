using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Argentini.Fdeploy.Domain;

public sealed class StorageRunner(Settings settings, List<string> exceptions, CancellationTokenSource cancellationTokenSource, string workingPath)
{
    #region App State Properties
    
    public SMB2Client Client { get; } = new();
    public Settings Settings { get; } = settings;
    public List<string> Exceptions { get; } = exceptions;
    public CancellationTokenSource CancellationTokenSource { get; } = cancellationTokenSource;
    public string WorkingPath { get; } = workingPath;
    
    #endregion

    #region Properties
    
    public List<FileObject> LocalFiles { get; } = [];
    public List<FileObject> ServerFiles { get; } = [];
    
    #endregion
    
    public async ValueTask ConnectAsync()
    {
        var isConnected = Client.Connect(Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport);
        var shares = new List<string>();

        if (isConnected)
        {
            var status = Client.Login(Settings.ServerConnection.Domain, Settings.ServerConnection.UserName, Settings.ServerConnection.Password);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                shares = Client.ListShares(out status);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    if (shares.Contains(Settings.ServerConnection.ShareName, StringComparer.OrdinalIgnoreCase) == false)
                    {
                        Exceptions.Add("Network share not found on the server");
                        await CancellationTokenSource.CancelAsync();
                    }
                }

                else
                {
                    Exceptions.Add("Could not retrieve server shares list");
                    await CancellationTokenSource.CancelAsync();
                }
            }

            else
            {
                Exceptions.Add("Server authentication failed");
                await CancellationTokenSource.CancelAsync();
            }
        }        

        else
        {
            Exceptions.Add("Could not connect to the server");
            await CancellationTokenSource.CancelAsync();
        }
        
        if (CancellationTokenSource.IsCancellationRequested)
            Disconnect();
    }

    public void Disconnect()
    {
        if (Client.IsConnected == false)
            return;

        try
        {
            Client.Logoff();
        }

        finally
        {
            Client.Disconnect();
        }
    }

    public async ValueTask RunDeploymentAsync()
    {
        #region Read All Local Files

        await RecurseLocalPathAsync(WorkingPath);
        await RecurseSmbPathAsync(Settings.Paths.RemoteRootPath.SetSmbPathSeparators());

        #endregion
    }

    private async ValueTask RecurseSmbPathAsync(string path)
    {
        var fileStore = Client.TreeConnect(Settings.ServerConnection.ShareName, out var status);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = fileStore.CreateFile(out var directoryHandle, out var fileStatus, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                status = fileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
                status = fileStore.CloseFile(directoryHandle);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    foreach (var item in fileList)
                    {
                        var file = (FileDirectoryInformation)item;

                        if (file.FileName is "." or "..")
                            continue;
                        
                        var isDirectory = (file.FileAttributes & FileAttributes.Directory) == FileAttributes.Directory;
                        
                        if (isDirectory)
                            await RecurseSmbPathAsync($"{path}\\{file.FileName}");

                        ServerFiles.Add(new FileObject
                        {
                            FullPath = $"{path}\\{file.FileName}",
                            FilePath =  $"{path}",
                            FileName = file.FileName,
                            LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                            FileSizeBytes = file.Length
                        });
                    }
                }
            }
        }        
    }

    private async ValueTask RecurseLocalPathAsync(string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            await RecurseLocalPathAsync(subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path))
        {
            var file = new FileInfo(filePath);
            
            LocalFiles.Add(new FileObject
            {
                FullPath = filePath,
                FilePath = file.DirectoryName ?? string.Empty,
                FileName = file.Name,
                LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                FileSizeBytes = file.Length
            });
        }
    }
}