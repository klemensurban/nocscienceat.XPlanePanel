namespace nocscienceat.XPlanePanel;

public interface IPanelHandler : IAsyncDisposable
{
    string PanelName { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken);
    Task DisconnectAsync();
}
