namespace nocscienceat.XPlanePanel;

/// <summary>
/// Defines the contract for a panel handler that manages connection and communication with a hardware panel.
/// </summary>
public interface IPanelHandler : IAsyncDisposable
{
    /// <summary>
    /// Gets the display name of the panel.
    /// </summary>
    string PanelName { get; }

    /// <summary>
    /// Gets a value indicating whether the panel is currently connected.
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Asynchronously connects to the panel.
    /// </summary>
    /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
    /// <returns>A task that represents the asynchronous connect operation.</returns>
    Task ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Asynchronously disconnects from the panel.
    /// </summary>
    /// <returns>A task that represents the asynchronous disconnect operation.</returns>
    Task DisconnectAsync();
}
