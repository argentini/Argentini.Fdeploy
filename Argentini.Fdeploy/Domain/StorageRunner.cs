using Argentini.Fdeploy.ConsoleBusy;
using SMBLibrary.Client;

namespace Argentini.Fdeploy.Domain;

public sealed class StorageRunner(Settings settings, List<string> exceptions, CancellationTokenSource cancellationTokenSource, string workingPath)
{
    #region App State Properties
    
    public SMB2Client Client { get; } = new ();
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
    
    public async ValueTask RunDeploymentAsync()
    {
        #region Connect to Server

        await Spinner.StartAsync($"Connecting to {Settings.ServerConnection.ServerAddress}...", async spinner =>
        {
            await this.ConnectAsync();

            if (CancellationTokenSource.IsCancellationRequested)
            {
                spinner.Fail($"Connecting to {Settings.ServerConnection.ServerAddress}... Failed!");
                this.Disconnect();
                return;
            }

            spinner.Text = $"Connecting to {Settings.ServerConnection.ServerAddress}... Success!";

        }, Patterns.Dots, Patterns.Line);

        if (CancellationTokenSource.IsCancellationRequested)
            return;
        
        #endregion

        // await Spinner.StartAsync("Test copy...", async spinner =>
        // {
        //     await CopyFileAsync("/Users/magic/Developer/Argentini.Fdeploy/clean.sh", $@"{Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh", spinner);
        //     
        //     if (CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test copy... Failed!");
        //     else
        //         spinner.Text = "Test copy... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (CancellationTokenSource.IsCancellationRequested)
        //     return;
        //
        // await Spinner.StartAsync("Test delete...", async spinner =>
        // {
        //     await DeleteServerFileAsync($@"{Settings.Paths.RemoteRootPath}\wwwroot\xxxx\abc\clean.sh", spinner);
        //     
        //     if (CancellationTokenSource.IsCancellationRequested)
        //         spinner.Fail("Test delete... Failed!");
        //     else
        //         spinner.Text = "Test delete... Success!";
        //
        // }, Patterns.Dots, Patterns.Line);
        //
        // if (CancellationTokenSource.IsCancellationRequested)
        //     return;
        
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

        FileStore = await FileStore.EnsureFileStoreAsync(this);
            
        if (FileStore is null)
            return;
        
        await Spinner.StartAsync("Indexing server files...", async spinner =>
        {
            await FileStore.RecurseSmbPathAsync(Settings.Paths.RemoteRootPath.NormalizeSmbPath(), 0, ServerFiles, spinner, "Indexing server files...", this);

            if (CancellationTokenSource.IsCancellationRequested)
                spinner.Fail("Indexing server files... Failed!");
            else
                spinner.Text = $"Indexing server files... {ServerFiles.Count:N0} files... Success!";

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

    private async ValueTask RecurseLocalPathAsync(string path)
    {
        foreach (var subdir in Directory.GetDirectories(path))
        {
            var directory = new DirectoryInfo(subdir);
            
            if ((directory.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
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

                if ((file.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden)
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