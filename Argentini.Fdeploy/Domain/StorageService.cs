using SMBLibrary;
using SMBLibrary.Client;

namespace Argentini.Fdeploy.Domain;

public sealed class StorageService(FdeployAppState appState)
{
    public SMB2Client Client { get; } = new();
    public FdeployAppState AppState { get; set; } = appState;

    public async ValueTask ConnectAsync()
    {
        var isConnected = Client.Connect(AppState.Settings.ServerConnection.ServerAddress, SMBTransportType.DirectTCPTransport);
        var shares = new List<string>();
        
        if (isConnected)
        {
            var status = Client.Login(AppState.Settings.ServerConnection.Domain, AppState.Settings.ServerConnection.UserName, AppState.Settings.ServerConnection.Password);

            if (status == NTStatus.STATUS_SUCCESS)
            {
                shares = Client.ListShares(out status);

                if (status == NTStatus.STATUS_SUCCESS)
                {
                    if (shares.Contains(AppState.Settings.ServerConnection.ShareName, StringComparer.OrdinalIgnoreCase) == false)
                    {
                        AppState.Exceptions.Add("Network share not found on the server");
                        await AppState.CancellationTokenSource.CancelAsync();
                    }
                }

                else
                {
                    AppState.Exceptions.Add("Could not retrieve server shares list");
                    await AppState.CancellationTokenSource.CancelAsync();
                }
            }

            else
            {
                AppState.Exceptions.Add("Server authentication failed");
                await AppState.CancellationTokenSource.CancelAsync();
            }
        }        

        else
        {
            AppState.Exceptions.Add("Could not connect to the server");
            await AppState.CancellationTokenSource.CancelAsync();
        }
        
        if (AppState.CancellationTokenSource.IsCancellationRequested)
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
}