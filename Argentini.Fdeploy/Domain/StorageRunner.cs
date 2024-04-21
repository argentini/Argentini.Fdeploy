using SMBLibrary;
using SMBLibrary.Client;

namespace Argentini.Fdeploy.Domain;

public sealed class StorageRunner(Settings settings, List<string> exceptions, CancellationTokenSource cancellationTokenSource)
{
    public SMB2Client Client { get; } = new();
    public Settings Settings { get; } = settings;
    public List<string> Exceptions { get; } = exceptions;
    public CancellationTokenSource CancellationTokenSource { get; } = cancellationTokenSource;

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
}