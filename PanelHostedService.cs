using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Interfaces;

namespace nocscienceat.XPlanePanel;

public class PanelHostedService : IHostedService
{
    private readonly IEnumerable<IPanelHandler> _panels;
    private readonly IXPlaneWebConnector _connector;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly ILogger<PanelHostedService> _logger;


    public PanelHostedService(IEnumerable<IPanelHandler> panels, IXPlaneWebConnector connector, IHostApplicationLifetime appLifetime, 
        ILogger<PanelHostedService> logger)
    {
        _panels = panels;
        _connector = connector;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting ...");

        // Wait for X-Plane to be available before connecting
        await _connector.WaitUntilAvailableAsync(cancellationToken: cancellationToken);

        // When X-Plane closes the WebSocket cleanly, request application shutdown
        _connector.ConnectionClosed += () =>
        {
            _logger.LogInformation("X-Plane connection closed, requesting application shutdown ...");
            Task.Delay(5000); 
            _appLifetime.StopApplication();
        };

        _connector.Start();

        var connectTasks = new List<Task>(_panels is ICollection<IPanelHandler> c ? c.Count : 0);

        // Connect all enabled panels
        foreach (IPanelHandler panel in _panels)
        {
            connectTasks.Add(panel.ConnectAsync(cancellationToken));
        }

        await Task.WhenAll(connectTasks);

        _logger.LogInformation("All panels initialized");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Shutting down ...");

        var disconnectTasks = new List<Task>(_panels is ICollection<IPanelHandler> c ? c.Count : 0);

        foreach (var panel in _panels)
        {
            disconnectTasks.Add(panel.DisconnectAsync());
        }

        await Task.WhenAll(disconnectTasks);

        try
        {
            await _connector.StopAsync();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Connector stop was canceled");
        }

        _logger.LogInformation("Shutdown complete");
    }
}
