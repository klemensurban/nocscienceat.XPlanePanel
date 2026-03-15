using nocscienceat.XPlanePanel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlanePanel.Services;
using nocscienceat.XPlaneWebConnector.Interfaces;
using System.IO.Ports;
using System.Threading.Channels;

namespace nocscienceat.XPlanePanel;

/// <summary>
/// Base class for JavaSimulator serial panels. Adds serial port communication
/// (UART with "command,value;" framing) on top of the universal panel plumbing.
/// Closes the generic with <see cref="SerialPanelConfig"/> so all serial panels
/// share PortName/BaudRate configuration.
/// </summary>
public abstract class JavaSimulatorPanelHandlerBase : PanelHandlerBase<SerialPanelConfig>
{
    protected SerialPort? _serialPort;
    private string _receiveBuffer = "";
    private readonly SemaphoreSlim _bufferLock = new(1, 1);

    // Dedicated serial-write queue: keeps serial writes ordered but off the panel work task.
    private readonly Channel<string> _serialWriteQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private Task? _serialWriteTask;

    public override bool IsConnected => _serialPort?.IsOpen ?? false;

    protected JavaSimulatorPanelHandlerBase(IXPlaneWebConnector connector, IConfiguration configuration, ILogger logger,
        IDataRefCommandProvider? overrideProvider = null)
        : base(connector, configuration, logger, overrideProvider)
    {
    }

    protected abstract Task SubscribeToDataRefsAsync(CancellationToken cancellationToken);
    protected abstract Task ProcessCommandAsync(string command, string value);

    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        if (_config.PortName == "disable")
        {
            _logger.LogInformation("{Panel} serial port is disabled", PanelName);
            return;
        }

        _serialPort = new SerialPort(_config.PortName, _config.BaudRate)
        {
            DtrEnable = true,
            RtsEnable = true
        };
        _serialPort.Open();
        
        await Task.Delay(500, cancellationToken);
        _serialPort.DiscardInBuffer();
        _serialPort.DiscardOutBuffer();
        _receiveBuffer = ""; 

        // Start the serial write pump
        _serialWriteTask = Task.Run(() => DrainSerialWriteQueueAsync(cancellationToken), cancellationToken);

        _serialPort.DataReceived += OnDataReceived; 

        if (_serialPort.IsOpen)
        {
            _logger.LogInformation("{Panel} connected on {Port}", PanelName, _config.PortName);
            await SubscribeToDataRefsAsync(cancellationToken);
        }
    }

    protected override async Task OnDisconnectingAsync()
    {
        _serialWriteQueue.Writer.TryComplete();

        if (_serialWriteTask is not null)
        {
            try { await _serialWriteTask; }
            catch (OperationCanceledException) { }
        }

        if (_serialPort?.IsOpen == true)
        {
            try { _serialPort.WriteLine("DOFF,1;"); } catch { }
            _serialPort.Close();
        }
        _serialPort?.Dispose();
        _serialPort = null;
    }

    protected void SendToHardware(string command, string value)
    {
        // Non-blocking: enqueue for the serial-write pump
        _serialWriteQueue.Writer.TryWrite($"{command},{value};");
    }

    protected void SendToHardware(string command)
    {
        _serialWriteQueue.Writer.TryWrite($"{command};");
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        _ = EnqueueReceivedDataAsync();
    }

    /// <summary>
    /// Parses complete commands from the serial buffer and enqueues each
    /// as a work item. The actual processing happens on the single panel task.
    /// </summary>
    private async Task EnqueueReceivedDataAsync()
    {
        await _bufferLock.WaitAsync();
        try
        {
            // Append incoming data to buffer
            var incoming = _serialPort!.ReadExisting();
            
            _receiveBuffer += incoming;
            _logger.LogDebug("serial-buffer: {buf}", _receiveBuffer);

            // Process all complete commands (ending with newline or semicolon)
            while (true)
            {
                // Find the first delimiter (newline or semicolon)
                var newlineIndex = _receiveBuffer.IndexOf('\n');
                var semicolonIndex = _receiveBuffer.IndexOf(';');

                var delimiterIndex = (newlineIndex, semicolonIndex) switch
                {
                    ( >= 0, >= 0) => Math.Min(newlineIndex, semicolonIndex),
                    ( >= 0, < 0) => newlineIndex,
                    ( < 0, >= 0) => semicolonIndex,
                    _ => -1
                };

                if (delimiterIndex < 0)
                    break; // No complete command yet; keep buffer for next data

                var command = _receiveBuffer[..delimiterIndex].Trim('\n', '\r', ' ', ';');
                _receiveBuffer = _receiveBuffer[(delimiterIndex + 1)..];
                
                if (string.IsNullOrWhiteSpace(command))
                    continue;
                
                // Parse command,value format and enqueue for the single panel task
                var parts = command.Split(',');
                if (parts.Length >= 2)
                {
                    var cmd = parts[0].Trim();
                    var val = parts[1].Trim();
                    _logger.LogDebug("{Panel} command: {Cmd}={Val}", PanelName, cmd, val);
                    EnqueueWork(() => ProcessCommandAsync(cmd, val));
                }
                else
                {
                    _logger.LogDebug("{Panel} received malformed command: '{Command}'", PanelName, command);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Panel} data receive error", PanelName);
        }
        finally
        {
            _bufferLock.Release();
        }
    }

    /// <summary>
    /// Background pump that sequentially writes to the serial port.
    /// This keeps serial writes ordered but off the panel work task.
    /// </summary>
    private async Task DrainSerialWriteQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var message in _serialWriteQueue.Reader.ReadAllAsync(ct))
            {
                if (_serialPort?.IsOpen == true)
                {
                    try
                    {
                        _serialPort.WriteLine(message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "{Panel} serial write error", PanelName);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _bufferLock.Dispose();
    }
}
