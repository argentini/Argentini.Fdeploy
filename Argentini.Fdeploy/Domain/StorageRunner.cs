using System.Net.Sockets;
using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace Argentini.Fdeploy.Domain;

public sealed class StorageRunner(Settings settings, List<string> exceptions, CancellationTokenSource cancellationTokenSource, string workingPath)
{
    #region App State Properties
    
    public SMB2Client Client { get; } = new();
    public ISMBFileStore? FileStore { get; set; }
    public Settings Settings { get; } = settings;
    public List<string> Exceptions { get; } = exceptions;
    public CancellationTokenSource CancellationTokenSource { get; } = cancellationTokenSource;
    public string WorkingPath { get; } = workingPath;
    
    #endregion

    #region Properties

    public string PublishPath { get; set; } = string.Empty;
    public string TrimmablePublishPath { get; set; } = string.Empty;
    public List<FileObject> LocalFiles { get; } = [];
    public List<FileObject> ServerFiles { get; } = [];
    
    #endregion
    
    public async ValueTask ConnectAsync()
    {
        var serverAvailable = false;
        
        using (var client = new TcpClient())
        {
            try
            {
                var result = client.BeginConnect(Settings.ServerConnection.ServerAddress, 445, null, null);
                
                serverAvailable = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(Settings.ServerConnection.ConnectTimeoutMs));
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
            Exceptions.Add("Server is not responding");
            await CancellationTokenSource.CancelAsync();
            return;
        }
        
        var isConnected = Client.Connect(Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport, Settings.ServerConnection.ResponseTimeoutMs);
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
        #region Connect to Server

        await Spinner.StartAsync($"Connecting to {Settings.ServerConnection.ServerAddress}...", async spinner =>
        {
            await ConnectAsync();

            if (CancellationTokenSource.IsCancellationRequested)
            {
                spinner.Fail($"Connecting to {Settings.ServerConnection.ServerAddress}... Failed!");
                Disconnect();
                return;
            }

            spinner.Text = $"Connecting to {Settings.ServerConnection.ServerAddress}... Success!";

        }, Patterns.Dots, Patterns.Line);

        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        await Spinner.StartAsync("Test copy...", async spinner =>
        {
            await CopyFileAsync("/Users/magic/Developer/Argentini.Fdeploy/clean.sh", $@"{Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh", spinner);
            
            if (CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Test copy... Failed!");
            else
                spinner.Text = "Test copy... Success!";
        
        }, Patterns.Dots, Patterns.Line);
        
        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        #region Publish Project

        PublishPath = $"{WorkingPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        TrimmablePublishPath = PublishPath.TrimPath();

        var sb = new StringBuilder();

        await Spinner.StartAsync($"Publishing project {Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{Settings.Project.TargetFramework:N1}", $"{WorkingPath}{Path.DirectorySeparatorChar}{Settings.Project.ProjectFilePath}", "-c", Settings.Project.BuildConfiguration, "-o", PublishPath, $"/p:EnvironmentName={Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"Publishing project {Settings.Project.ProjectFileName}... Failed!");
                    Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"Published project {Settings.Project.ProjectFileName}... Success!";
            }

            catch (Exception e)
            {
                spinner.Fail($"Publishing project {Settings.Project.ProjectFileName}... Failed!");
                Exceptions.Add($"Could not publish the project; {e.Message}");
                await CancellationTokenSource.CancelAsync();
            }
            
        }, Patterns.Dots, Patterns.Line);

        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            await RecurseLocalPathAsync(PublishPath);

            if (CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing local files... Failed!");
            else        
                spinner.Text = $"Indexing local files... {LocalFiles.Count:N0} files... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
       
        #region Index Server Files
        
        await Spinner.StartAsync("Indexing server files...", async spinner =>
        {
            await RecurseSmbPathAsync(Settings.Paths.RemoteRootPath.NormalizeSmbPath(), spinner);

            if (CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing server files... Failed!");
            else
                spinner.Text = "Indexing server files... Success!";

        }, Patterns.Dots, Patterns.Line);

        if (CancellationTokenSource.IsCancellationRequested)
            return;

        #endregion
        
        #region Static File Sync
        
        // foreach (var path in Settings.Paths.StaticFilePaths)
        // {
        // }
        
        #endregion

        #region Process Static File Copies
        
        #endregion

        #region Take Server Offline
        
        #endregion

        #region Process File Deletions
        
        #endregion

        #region File Sync
        
        #endregion
        
        #region Process File Copies
        
        #endregion
        
        #region Bring Server Online
        
        #endregion
        
        #region Local Cleanup
        
        #endregion
    }

    private async ValueTask RecurseSmbPathAsync(string path, Spinner spinner, int level = 0)
    {
        var status = NTStatus.STATUS_SUCCESS;
        
        if (FileStore is null)
        {
            FileStore = Client.TreeConnect(Settings.ServerConnection.ShareName, out status);

            if (status != NTStatus.STATUS_SUCCESS)
            {
                Exceptions.Add($"Could not connect to the file share `{Settings.ServerConnection.ShareName}`");
                await CancellationTokenSource.CancelAsync();
                return;
            }
        }

        status = FileStore.CreateFile(out var directoryHandle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = FileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
            status = FileStore.CloseFile(directoryHandle);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                foreach (var item in fileList)
                {
                    if (CancellationTokenSource.IsCancellationRequested)
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
                            if (Settings.Paths.RelativeIgnorePaths.Contains(filePath.NormalizePath().TrimStart(Settings.Paths.RemoteRootPath).TrimPath()) || Settings.Paths.IgnoreFoldersNamed.Contains(file.FileName))
                                continue;
                            
                            if (level == 0)
                                spinner.Text = $"Indexing server files... {file.FileName}/...";
                            
                            await RecurseSmbPathAsync($"{path}\\{file.FileName}", spinner, level + 1);
                        }

                        else
                        {
                            if (Settings.Paths.RelativeIgnoreFilePaths.Contains(filePath.NormalizePath().TrimStart(Settings.Paths.RemoteRootPath).TrimPath()) || Settings.Paths.IgnoreFilesNamed.Contains(file.FileName))
                                continue;

                            ServerFiles.Add(new FileObject
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
                        Exceptions.Add($"Could process server file `{(file is null ? item.ToString() : file.FileName)}`");
                        await CancellationTokenSource.CancelAsync();
                    }
                }
            }
            else
            {
                Exceptions.Add($"Could not read the contents of server path `{path}`");
                await CancellationTokenSource.CancelAsync();
            }
        }
        else
        {
            Exceptions.Add($"Cannot write to server path `{path}`");
            await CancellationTokenSource.CancelAsync();
        }
    }

    private async ValueTask RecurseLocalPathAsync(string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                continue;
            
            var trimmed = subdir.TrimPath().TrimStart(TrimmablePublishPath).TrimPath(); 
            
            if (Settings.Paths.RelativeIgnorePaths.Contains(trimmed) || Settings.Paths.IgnoreFoldersNamed.Contains(subdir.GetLastPathSegment()))
                continue;
            
            await RecurseLocalPathAsync(subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path))
        {
            try
            {
                var file = new FileInfo(filePath);

                if ((file.Attributes & System.IO.FileAttributes.Hidden) == System.IO.FileAttributes.Hidden)
                    continue;
                
                var trimmed = filePath.TrimPath().TrimStart(TrimmablePublishPath).TrimPath(); 

                if (Settings.Paths.RelativeIgnoreFilePaths.Contains(trimmed) || Settings.Paths.IgnoreFilesNamed.Contains(file.Name))
                    continue;

                LocalFiles.Add(new FileObject
                {
                    FullPath = filePath,
                    FilePath = file.DirectoryName.TrimPath(),
                    FileName = file.Name,
                    LastWriteTime = file.LastWriteTime.ToFileTimeUtc(),
                    FileSizeBytes = file.Length
                });
            }
            catch
            {
                Exceptions.Add($"Could process local file `{filePath}`");
                await CancellationTokenSource.CancelAsync();
            }
        }
    }

    private async ValueTask<bool> EnsureFileStoreAsync()
    {
        if (CancellationTokenSource.IsCancellationRequested)
            return false;

        if (FileStore is not null)
            return true;

        FileStore = Client.TreeConnect(Settings.ServerConnection.ShareName, out var status);

        if (status == NTStatus.STATUS_SUCCESS)
            return true;
        
        Exceptions.Add($"Could not connect to the file share `{Settings.ServerConnection.ShareName}`");
        await CancellationTokenSource.CancelAsync();

        return false;
    }
    
    private async ValueTask<bool> FileExistsAsync(string filePath)
    {
        if (CancellationTokenSource.IsCancellationRequested)
            return false;

        if (await EnsureFileStoreAsync() == false)
            return false;

        if (FileStore is null)
            return false;

        var remotePath = filePath.NormalizeSmbPath();

        var status = FileStore.CreateFile(out var fileHandle, out _, remotePath, AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.Read, CreateDisposition.FILE_OPEN, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

        if (fileHandle is not null)
            FileStore.CloseFile(fileHandle);
            
        return status == NTStatus.STATUS_SUCCESS;
    }

    private async ValueTask<bool> PathExistsAsync(string destinationPath)
    {
        if (CancellationTokenSource.IsCancellationRequested)
            return false;

        if (await EnsureFileStoreAsync() == false)
            return false;

        if (FileStore is null)
            return false;
        
        var status = FileStore.CreateFile(out var fileHandle, out _, destinationPath.NormalizeSmbPath(), AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

        if (fileHandle is not null)
            FileStore.CloseFile(fileHandle);

        return status == NTStatus.STATUS_SUCCESS;
    }

    private async ValueTask EnsurePathExists(string destinationPath, Spinner? spinner = null)
    {
        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (await EnsureFileStoreAsync() == false)
            return;

        if (FileStore is null)
            return;

        if (await PathExistsAsync(destinationPath))
            return;

        var segments = destinationPath.NormalizeSmbPath().Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var buildingPath = string.Empty;

        foreach (var segment in segments)
        {
            if (buildingPath != string.Empty)
                buildingPath += '\\';
            
            buildingPath += segment;
            
            if (await PathExistsAsync(buildingPath))
                continue;
            
            var success = true;
            var retries = Settings.RetryCount > 0 ? Settings.RetryCount : 1;
        
            for (var attempt = 0; attempt < retries; attempt++)
            {
                var status = FileStore.CreateFile(out _, out _, $"{buildingPath}", AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, CreateDisposition.FILE_CREATE, CreateOptions.FILE_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);

                if (status == NTStatus.STATUS_SUCCESS)
                    break;

                success = false;

                if (spinner is not null)
                {
                    var text = spinner.Text.TrimEnd($" Retry {attempt}...");
                    spinner.Text = $"{text} Retry {attempt + 1}...";
                }

                await Task.Delay(Settings.WriteRetryDelaySeconds * 1000);
            }

            if (success)
                continue;
            
            Exceptions.Add($"Failed to create directory after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{buildingPath}`");
            await CancellationTokenSource.CancelAsync();
            break;
        }
    }
    
    private async ValueTask CopyFileAsync(FileObject sourceFo)
    {
        var relativePathWithFile = sourceFo.FullPath.TrimPath().TrimStart(TrimmablePublishPath).TrimPath();
        
        await CopyFileAsync(relativePathWithFile, relativePathWithFile);
    }
    
    private async ValueTask CopyFileAsync(string sourceFilePath, string destinationFilePath, Spinner? spinner = null)
    {
        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        if (await EnsureFileStoreAsync() == false)
            return;

        if (FileStore is null)
            return;

        var smbFilePath = destinationFilePath.NormalizeSmbPath();
        var destinationPathWithoutFile = smbFilePath[..smbFilePath.LastIndexOf('\\')];

        await EnsurePathExists(destinationPathWithoutFile, spinner);

        if (CancellationTokenSource.IsCancellationRequested)
            return;

        var localFileStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read);
        var success = true;
        var retries = Settings.RetryCount > 0 ? Settings.RetryCount : 1;
        
        for (var attempt = 0; attempt < retries; attempt++)
        {
            var fileExists = await FileExistsAsync(smbFilePath);

            var status = FileStore.CreateFile(out var fileHandle, out _, smbFilePath, AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE, FileAttributes.Normal, ShareAccess.None, fileExists ? CreateDisposition.FILE_OVERWRITE : CreateDisposition.FILE_CREATE, CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT, null);
            
            if (status == NTStatus.STATUS_SUCCESS)
            {
                var writeOffset = 0;
                
                while (localFileStream.Position < localFileStream.Length)
                {
                    var buffer = new byte[(int)Client.MaxWriteSize];
                    var bytesRead = localFileStream.Read(buffer, 0, buffer.Length);
                    
                    if (bytesRead < (int)Client.MaxWriteSize)
                    {
                        Array.Resize(ref buffer, bytesRead);
                    }
                    
                    status = FileStore.WriteFile(out _, fileHandle, writeOffset, buffer);

                    if (status != NTStatus.STATUS_SUCCESS)
                    {
                        success = false;
                        break;
                    }

                    writeOffset += bytesRead;
                }

                status = FileStore.CloseFile(fileHandle);
            }

            if (success)
                break;

            if (spinner is not null)
            {
                var text = spinner.Text.TrimEnd($" Retry {attempt}...");
                spinner.Text = $"{text} Retry {attempt + 1}...";
            }

            await Task.Delay(Settings.WriteRetryDelaySeconds * 1000);
        }

        if (success == false)
        {
            Exceptions.Add($"Failed to write file after {retries:N0} {(retries == 1 ? "retry" : "retries")}: `{smbFilePath}`");
            await CancellationTokenSource.CancelAsync();
        }
    }
}