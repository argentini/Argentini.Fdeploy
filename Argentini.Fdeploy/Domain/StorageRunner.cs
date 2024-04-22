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

    public string PublishPath { get; set; } = string.Empty;
    public string TrimmablePublishPath { get; set; } = string.Empty;
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
        #region Publish Project

        PublishPath = $"{WorkingPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        TrimmablePublishPath = PublishPath.TrimPath();

        try
        {
            await Console.Out.WriteAsync($"Publishing project {Settings.Project.ProjectFileName}...");

            var sb = new StringBuilder();
            var cmd = Cli.Wrap("dotnet")
                .WithArguments(new [] { "publish", "--framework", $"net{Settings.Project.TargetFramework:N1}", $"{WorkingPath}{Path.DirectorySeparatorChar}{Settings.Project.ProjectFilePath}", "-c", Settings.Project.BuildConfiguration, "-o", PublishPath, $"/p:EnvironmentName={Settings.Project.EnvironmentName}" })
                .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                .WithStandardErrorPipe(PipeTarget.Null);
		    
            var result = await cmd.ExecuteAsync();

            if (result.IsSuccess == false)
            {
                await Console.Out.WriteLineAsync(" Failed!");
                Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                await CancellationTokenSource.CancelAsync();
                return;
            }

            await Console.Out.WriteLineAsync(" Success!");
        }

        catch (Exception e)
        {
            await Console.Out.WriteLineAsync(" Failed!");
            Exceptions.Add($"Could not publish the project; {e.Message}");
            await CancellationTokenSource.CancelAsync();
            return;
        }
        
        #endregion
        
        #region Index Local Files

        await Console.Out.WriteAsync("Scanning local files...");
        
        await RecurseLocalPathAsync(PublishPath);

        if (CancellationTokenSource.IsCancellationRequested)
            await Console.Out.WriteLineAsync(" Failed!");
        else        
            await Console.Out.WriteLineAsync($" {LocalFiles.Count:N0} files... Success!");
        
        #endregion
        
        #region Connect to Server
        
        await Console.Out.WriteAsync($"Connecting to {Settings.ServerConnection.ServerAddress}...");
        
        await ConnectAsync();

        if (CancellationTokenSource.IsCancellationRequested)
        {
            await Console.Out.WriteLineAsync(" Failed!");
            Disconnect();
            return;
        }

        await Console.Out.WriteLineAsync(" Success!");
        
        #endregion
        
        #region Index Server Files
        
        await Console.Out.WriteAsync("Scanning server files...");

        await RecurseSmbPathAsync(Settings.Paths.RemoteRootPath.SetSmbPathSeparators());

        if (CancellationTokenSource.IsCancellationRequested)
            await Console.Out.WriteLineAsync(" Processing Failed!");
        else        
            await Console.Out.WriteLineAsync($" {ServerFiles.Count:N0} files... Success!");

        #endregion
    }

    private async ValueTask RecurseSmbPathAsync(string path, int level = 0)
    {
        var fileStore = Client.TreeConnect(Settings.ServerConnection.ShareName, out var status);

        if (status == NTStatus.STATUS_SUCCESS)
        {
            status = fileStore.CreateFile(out var directoryHandle, out _, path, AccessMask.GENERIC_READ, FileAttributes.Directory, ShareAccess.Read | ShareAccess.Write, CreateDisposition.FILE_OPEN, CreateOptions.FILE_DIRECTORY_FILE, null);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                status = fileStore.QueryDirectory(out var fileList, directoryHandle, "*", FileInformationClass.FileDirectoryInformation);
                status = fileStore.CloseFile(directoryHandle);

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
                                    await Console.Out.WriteAsync($" {file.FileName}/...");
                                
                                await RecurseSmbPathAsync($"{path}\\{file.FileName}", level + 1);
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
        else
        {
            Exceptions.Add($"Could not connect to the server share `{Settings.ServerConnection.ShareName}`");
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
}