using CliWrap.Buffered;

namespace Argentini.Fdeploy.Extensions;

public static class Network
{
    public static string GetServerPathPrefix(this AppState appState)
    {
        if (Identify.GetOsPlatform() == OSPlatform.OSX)
            return $"/Volumes/{appState.Settings.ServerConnection.ShareName}{(string.IsNullOrEmpty(appState.Settings.ServerConnection.RemoteRootPath) ? string.Empty : $"{Path.DirectorySeparatorChar}{appState.Settings.ServerConnection.RemoteRootPath}")}";

        if (Identify.GetOsPlatform() == OSPlatform.Windows)
            return $"{appState.Settings.WindowsMountLetter}:{(string.IsNullOrEmpty(appState.Settings.ServerConnection.RemoteRootPath) ? string.Empty : $"{Path.DirectorySeparatorChar}{appState.Settings.ServerConnection.RemoteRootPath}")}";

        return string.Empty;
    }
    
    public static async Task<bool> ConnectNetworkShareAsync(this AppState appState)
    {
        if (Identify.GetOsPlatform() == OSPlatform.OSX)
        {
            if (appState.CancellationTokenSource.IsCancellationRequested)
                return false;
            
            var mountScript =
                $"""
                tell application "Finder"
                    if (exists disk "{appState.Settings.ServerConnection.ShareName}") then
                        try
                            eject "{appState.Settings.ServerConnection.ShareName}"
                        end try
                    end if
                    mount volume "smb://{(string.IsNullOrEmpty(appState.Settings.ServerConnection.Domain) ? "" : appState.Settings.ServerConnection.Domain + ";")}{appState.Settings.ServerConnection.UserName}:{appState.Settings.ServerConnection.Password}@{appState.Settings.ServerConnection.ServerAddress}/{appState.Settings.ServerConnection.ShareName}"
                end tell
                """;

            await File.WriteAllTextAsync(appState.AppleScriptPath, mountScript);
            
            var cmdResult = await Cli.Wrap("osascript")
                .WithArguments([appState.AppleScriptPath])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            
            if (File.Exists(appState.AppleScriptPath))
                File.Delete(appState.AppleScriptPath);

            if (cmdResult.IsSuccess)
                return true;
            
            appState.Exceptions.Add($"Could not mount network share `{appState.Settings.ServerConnection.ServerAddress}/{appState.Settings.ServerConnection.ShareName}`");
            await appState.CancellationTokenSource.CancelAsync();
            return false;
        }

        if (Identify.GetOsPlatform() == OSPlatform.Windows)
        {
            await Cli.Wrap("powershell")
                .WithArguments(["net", "use", $"{appState.Settings.WindowsMountLetter}:", "/delete", "/y"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();
            
            var cmdResult = await Cli.Wrap("powershell")
                .WithArguments(["net", "use", $"{appState.Settings.WindowsMountLetter}:", $@"\\{appState.Settings.ServerConnection.ServerAddress}\{appState.Settings.ServerConnection.ShareName}", $"/user:{(string.IsNullOrEmpty(appState.Settings.ServerConnection.Domain) ? string.Empty : $@"{appState.Settings.ServerConnection.Domain}\")}{appState.Settings.ServerConnection.UserName}", appState.Settings.ServerConnection.Password])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            //var cl = $@"net use {appState.Settings.WindowsMountLetter}: \\{appState.Settings.ServerConnection.ServerAddress}\{appState.Settings.ServerConnection.ShareName} /user:{(string.IsNullOrEmpty(appState.Settings.ServerConnection.Domain) ? string.Empty : $@"{appState.Settings.ServerConnection.Domain}\")}{appState.Settings.ServerConnection.UserName} {appState.Settings.ServerConnection.Password}";
            
            if (cmdResult.IsSuccess)
                return true;
            
            appState.Exceptions.Add($@"Could not mount network share \\{appState.Settings.ServerConnection.ServerAddress}\{appState.Settings.ServerConnection.ShareName}");
            await appState.CancellationTokenSource.CancelAsync();

            return false;
        }

        appState.Exceptions.Add("Unsupported platform");
        await appState.CancellationTokenSource.CancelAsync();
        return false;
    }
    
    public static async Task<bool> DisconnectNetworkShareAsync(this AppState appState)
    {
        if (Identify.GetOsPlatform() == OSPlatform.OSX)
        {
            var ejectScript =
                $"""
                tell application "Finder"
                    if (exists disk "{appState.Settings.ServerConnection.ShareName}") then
                        try
                            eject "{appState.Settings.ServerConnection.ShareName}"
                        end try
                    end if
                end tell
                """;

            await File.WriteAllTextAsync(appState.AppleScriptPath, ejectScript);
            
            var cmdResult = await Cli.Wrap("osascript")
                .WithArguments([appState.AppleScriptPath])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (File.Exists(appState.AppleScriptPath))
                File.Delete(appState.AppleScriptPath);

            if (cmdResult.IsSuccess)
                return true;
            
            appState.Exceptions.Add($"Could not unmount `{appState.Settings.ServerConnection.ServerAddress}/{appState.Settings.ServerConnection.ShareName}`");
            await appState.CancellationTokenSource.CancelAsync();
            return false;
        }

        if (Identify.GetOsPlatform() == OSPlatform.Windows)
        {
            var cmdResult = await Cli.Wrap("powershell")
                .WithArguments(["net", "use", $"{appState.Settings.WindowsMountLetter}:", "/delete", "/y"])
                .WithValidation(CommandResultValidation.None)
                .ExecuteBufferedAsync();

            if (cmdResult.IsSuccess)
                return true;
            
            appState.Exceptions.Add($@"Could not unmount network share \\{appState.Settings.ServerConnection.ServerAddress}\{appState.Settings.ServerConnection.ShareName}");
            await appState.CancellationTokenSource.CancelAsync();
            return false;
        }

        appState.Exceptions.Add("Unsupported platform");
        await appState.CancellationTokenSource.CancelAsync();
        return false;
    }
    
    public static long ComparableTime(this DateTime dateTime)
    {
        return new DateTimeOffset(
            dateTime.Year,
            dateTime.Month,
            dateTime.Day,
            dateTime.Hour,
            dateTime.Minute,
            dateTime.Second,
            0,
            TimeSpan.Zero
        ).ToFileTime();
    }
}