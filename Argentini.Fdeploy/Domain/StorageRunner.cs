using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary.Client;

namespace Argentini.Fdeploy.Domain;

public sealed class StorageRunner(
    Settings settings,
    List<string> exceptions,
    CancellationTokenSource cancellationTokenSource,
    string workingPath)
{
    #region App State Properties
    
    public string WorkingPath { get; } = workingPath;
    public SmbConfig SmbConfig { get; } = new()
    {
        CancellationTokenSource = cancellationTokenSource,
        Client = new SMB2Client(),
        Exceptions = exceptions,
        Settings = settings
    };

    #endregion

    #region Properties

    public string PublishPath { get; set; } = string.Empty;
    public string TrimmablePublishPath { get; set; } = string.Empty;
    public List<FileObject> LocalFiles { get; } = [];
    public List<FileObject> ServerFiles { get; } = [];
    
    #endregion

    public async ValueTask RunDeploymentAsync()
    {
        #region Connect to Server

        await Spinner.StartAsync($"Connecting to {SmbConfig.Settings.ServerConnection.ServerAddress}...", spinner =>
        {
            Smb.Connect(SmbConfig);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            {
                spinner.Fail($"Connecting to {SmbConfig.Settings.ServerConnection.ServerAddress}... Failed!");
                Smb.Disconnect(SmbConfig);
            
                return Task.CompletedTask;
            }

            spinner.Text = $"Connecting to {SmbConfig.Settings.ServerConnection.ServerAddress}... Success!";
            
            return Task.CompletedTask;
            
        }, Patterns.Dots, Patterns.Line);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        await Spinner.StartAsync("Test copy...", async spinner =>
        {
            SmbConfig.Spinner = spinner;
            
            await Smb.CopyFileAsync(SmbConfig, "/Users/magic/Developer/Argentini.Fdeploy/clean.sh", $@"{SmbConfig.Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh");
            
            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Test copy... Failed!");
            else
                spinner.Text = "Test copy... Success!";
        
        }, Patterns.Dots, Patterns.Line);
        
        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        await Spinner.StartAsync("Test delete folder...", async spinner =>
        {
            SmbConfig.Spinner = spinner;

            await Smb.DeleteServerFolderRecursiveAsync(SmbConfig, $@"{SmbConfig.Settings.Paths.RemoteRootPath}\wwwroot\xxxx");
            
            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Test delete folder... Failed!");
            else
                spinner.Text = "Test delete folder... Success!";
        
        }, Patterns.Dots, Patterns.Line);
        
        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;

        // await Spinner.StartAsync("Test delete...", async spinner =>
        // {
        //     SmbConfig.Spinner = spinner;
        //
        //     await Smb.DeleteServerFileAsync(SmbConfig, $@"{SmbConfig.Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh");
        //     
        //     if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test delete... Failed!");
        //     else
        //         spinner.Text = "Test delete... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
        //     return;
        
        #region Publish Project

        PublishPath = $"{WorkingPath}{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}publish";
        TrimmablePublishPath = PublishPath.TrimPath();

        var sb = new StringBuilder();

        await Spinner.StartAsync($"Publishing project {SmbConfig.Settings.Project.ProjectFileName}...", async spinner =>
        {
            try
            {
                var cmd = Cli.Wrap("dotnet")
                    .WithArguments(new [] { "publish", "--framework", $"net{SmbConfig.Settings.Project.TargetFramework:N1}", $"{WorkingPath}{Path.DirectorySeparatorChar}{SmbConfig.Settings.Project.ProjectFilePath}", "-c", SmbConfig.Settings.Project.BuildConfiguration, "-o", PublishPath, $"/p:EnvironmentName={SmbConfig.Settings.Project.EnvironmentName}" })
                    .WithStandardOutputPipe(PipeTarget.ToStringBuilder(sb))
                    .WithStandardErrorPipe(PipeTarget.Null);
		    
                var result = await cmd.ExecuteAsync();

                if (result.IsSuccess == false)
                {
                    spinner.Fail($"Publishing project {SmbConfig.Settings.Project.ProjectFileName}... Failed!");
                    SmbConfig.Exceptions.Add($"Could not publish the project; exit code: {result.ExitCode}");
                    await SmbConfig.CancellationTokenSource.CancelAsync();
                    return;
                }

                spinner.Text = $"Published project {SmbConfig.Settings.Project.ProjectFileName}... Success!";
            }

            catch (Exception e)
            {
                spinner.Fail($"Publishing project {SmbConfig.Settings.Project.ProjectFileName}... Failed!");
                SmbConfig.Exceptions.Add($"Could not publish the project; {e.Message}");
                await SmbConfig.CancellationTokenSource.CancelAsync();
            }
            
        }, Patterns.Dots, Patterns.Line);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
        
        #region Index Local Files

        await Spinner.StartAsync("Indexing local files...", async spinner =>
        {
            await RecurseLocalPathAsync(PublishPath);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing local files... Failed!");
            else        
                spinner.Text = $"Indexing local files... {LocalFiles.Count:N0} files... Success!";
            
        }, Patterns.Dots, Patterns.Line);
       
        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion
       
        #region Index Server Files

        await Smb.EnsureFileStoreAsync(SmbConfig);
            
        if (SmbConfig.FileStore is null)
            return;
        
        await Spinner.StartAsync("Indexing server files...", async spinner =>
        {
            SmbConfig.Files = ServerFiles;
            SmbConfig.Spinner = spinner;
            
            await Smb.RecurseSmbPathAsync(SmbConfig, SmbConfig.Settings.Paths.RemoteRootPath.NormalizeSmbPath(), 0);

            if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing server files... Failed!");
            else
                spinner.Text = $"Indexing server files... {ServerFiles.Count:N0} files... Success!";

        }, Patterns.Dots, Patterns.Line);

        if (SmbConfig.CancellationTokenSource.IsCancellationRequested)
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

    private async ValueTask RecurseLocalPathAsync(string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                continue;
            
            var trimmed = subdir.TrimPath().TrimStart(TrimmablePublishPath).TrimPath(); 
            
            if (SmbConfig.Settings.Paths.RelativeIgnorePaths.Contains(trimmed) || SmbConfig.Settings.Paths.IgnoreFoldersNamed.Contains(subdir.GetLastPathSegment()))
                continue;
            
            await RecurseLocalPathAsync(subdir);
        }
        
        foreach (var filePath in Directory.GetFiles(path))
        {
            try
            {
                var file = new FileInfo(filePath);

                if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
                    continue;
                
                var trimmed = filePath.TrimPath().TrimStart(TrimmablePublishPath).TrimPath(); 

                if (SmbConfig.Settings.Paths.RelativeIgnoreFilePaths.Contains(trimmed) || SmbConfig.Settings.Paths.IgnoreFilesNamed.Contains(file.Name))
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
                SmbConfig.Exceptions.Add($"Could process local file `{filePath}`");
                await SmbConfig.CancellationTokenSource.CancelAsync();
            }
        }
    }
}