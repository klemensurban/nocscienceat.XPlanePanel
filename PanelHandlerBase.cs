using nocscienceat.XPlanePanel.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using nocscienceat.XPlaneWebConnector.Interfaces;
using System.Threading.Channels;
using nocscienceat.XPlanePanel.Services;

namespace nocscienceat.XPlanePanel;

/// <summary>
/// Universal base class for all panel handlers. Provides the single-threaded work queue,
/// subscription helpers, and lifecycle management. Communication-channel-specific concerns
/// (serial, USB HID, network, …) belong in derived intermediate base classes.
/// <para>Generic on <typeparamref name="TConfig"/> so each panel family can define its own
/// configuration POCO. The config is bound from the <c>Panels:{PanelName}</c> section.</para>
/// </summary>
public abstract partial class PanelHandlerBase<TConfig> : IPanelHandler where TConfig : PanelConfig, new()
{
    /// <summary>The X-Plane web connector used to subscribe to datarefs and send commands.</summary>
    protected readonly IXPlaneWebConnector _connector;

    /// <summary>Logger instance scoped to the concrete panel type.</summary>
    protected readonly ILogger _logger;

    /// <summary>Strongly-typed configuration bound from <c>Panels:{PanelName}</c>.</summary>
    protected readonly TConfig _config;

    /// <summary>
    /// Optional provider that can override the default dataref/command registrations
    /// or define dataref/command registrations if the panel provides none in code but as json file
    /// </summary>
    private readonly IDataRefCommandProvider? _overrideProvider;

    /// <summary>
    /// Single work queue: both subscription callbacks and hardware commands enqueue here.
    /// One background task drains it — all panel state is accessed from a single thread.
    /// </summary>
    private readonly Channel<Func<Task>> _sequentialWorkQueue = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Background task that drains <see cref="_sequentialWorkQueue"/>.</summary>
    private Task? _workTask;

    /// <summary>Cancellation source linked to the external token passed to <see cref="ConnectAsync"/>.</summary>
    private CancellationTokenSource? _panelCts;

    /// <summary>Gets the unique display name of this panel (used for configuration binding and logging).</summary>
    public abstract string PanelName { get; }

    /// <summary>Gets a value indicating whether the panel's communication channel is currently open.</summary>
    public virtual bool IsConnected => false;

    /// <summary>
    /// Initializes a new instance of the <see cref="PanelHandlerBase{TConfig}"/> class.
    /// </summary>
    /// <param name="connector">The X-Plane web connector for dataref subscriptions and command execution.</param>
    /// <param name="configuration">Application configuration root used to bind the panel-specific section.</param>
    /// <param name="logger">Logger instance for diagnostic output.</param>
    /// <param name="overrideProvider">
    /// Optional provider that can override the default dataref/command registrations.
    /// or provide dataref/command registrations if the panel provides none in code but as json file -  
    ///  <see cref="DataRefCommandProvider"/> for details.
    /// </param>
    protected PanelHandlerBase(IXPlaneWebConnector connector, IConfiguration configuration, ILogger logger,
        IDataRefCommandProvider? overrideProvider = null)
    {
        _connector = connector;
        _logger = logger;
        _overrideProvider = overrideProvider;
        _config = configuration.GetSection($"Panels:{PanelName}").Get<TConfig>() ?? new TConfig();
    }

    /// <summary>
    /// Connects the panel by registering datarefs/commands, starting the sequential work queue,
    /// and delegating to <see cref="OnConnectedAsync"/> for channel-specific setup.
    /// </summary>
    /// <param name="cancellationToken">Token that signals the panel should shut down.</param>
    public virtual async Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("{Panel} is disabled", PanelName);
            return;
        }

        try
        {
            RegisterDataRefsAndCommands();
            _panelCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _workTask = Task.Run(() => DrainWorkQueueAsync(_panelCts.Token), _panelCts.Token);

            await OnConnectedAsync(_panelCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect {Panel}", PanelName);
        }
    }

    /// <summary>
    /// Registers datarefs and commands required by this panel.
    /// By default, does nothing. Subclasses can override to perform registrations.
    /// </summary>
    protected virtual void RegisterDataRefsAndCommands()
    {
    }

    /// <summary>
    /// Called after the work queue task has started. Subclasses should open their
    /// communication channel and subscribe to datarefs here.
    /// </summary>
    /// <param name="cancellationToken">Token that signals the panel should shut down.</param>
    protected abstract Task OnConnectedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Called during disconnect before the work queue is drained. Subclasses can
    /// send shutdown messages and close their communication channel here.
    /// </summary>
    protected virtual Task OnDisconnectingAsync() => Task.CompletedTask;

    /// <summary>
    /// Enqueues an async work item to be processed on the single panel task.
    /// Use this from subscription callbacks to ensure all panel state
    /// is accessed from a single thread.
    /// </summary>
    /// <param name="work">The asynchronous work item to enqueue.</param>
    protected void EnqueueWork(Func<Task> work)
    {
        _sequentialWorkQueue.Writer.TryWrite(work);
    }

    /// <summary>
    /// Enqueues a synchronous work item to be processed on the single panel task.
    /// Use this from subscription callbacks to ensure all panel state
    /// is accessed from a single thread.
    /// </summary>
    /// <param name="work">The synchronous work item to enqueue.</param>
    protected void EnqueueWork(Action work)
    {
        _sequentialWorkQueue.Writer.TryWrite(() => { work(); return Task.CompletedTask; });
    }

    // ===================================================================================
    // SubscribeEnqueuedAsync — routes connector callbacks through the !Panel! work queue.
    // Sorted by dataref type: double, float, int, string.
    // Each type has an Action<T> (sync) and Func<T, Task> (async) variant.
    // ===================================================================================

    /// <summary>
    /// Subscribes to a <see cref="double"/> dataref and routes the callback through the single
    /// worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Synchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Action<double> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (double value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to a <see cref="double"/> dataref and routes the async callback through the single
    /// worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Asynchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Func<double, Task> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (double value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to a <see cref="float"/> dataref and routes the callback through the single
    /// worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Synchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Action<float> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (float value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to a <see cref="float"/> dataref and routes the async callback through the single
    /// worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Asynchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Func<float, Task> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (float value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to an <see cref="int"/> dataref and routes the callback through the single
    /// worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Synchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Action<int> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (int value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to an <see cref="int"/> dataref and routes the async callback through the single
    /// worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Asynchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Func<int, Task> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (int value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to a <see cref="string"/> (byte-array / data) dataref and routes the callback
    /// through the single worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Synchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Action<string> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (string value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Subscribes to a <see cref="string"/> (byte-array / data) dataref and routes the async
    /// callback through the single worker task, enabling lock-free state access from panel logic.
    /// </summary>
    /// <param name="dataRefPath">The X-Plane dataref path to subscribe to.</param>
    /// <param name="callback">Asynchronous callback invoked with the new value.</param>
    protected Task SubscribeEnqueuedAsync(string dataRefPath, Func<string, Task> callback)
    {
        return _connector.SubscribeAsync(dataRefPath, (string value) => EnqueueWork(() => callback(value)));
    }

    /// <summary>
    /// Single background task that drains the work queue.
    /// All panel state (toggle fields, etc.) is accessed exclusively from this task.
    /// </summary>
    /// <param name="ct">Cancellation token that stops the drain loop.</param>
    private async Task DrainWorkQueueAsync(CancellationToken ct)
    {
        try
        {
            await foreach (Func<Task> work in _sequentialWorkQueue.Reader.ReadAllAsync(ct))
            {
                try
                {
                    await work();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Panel} work item error", PanelName);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// Gracefully disconnects the panel by cancelling the work queue, invoking
    /// <see cref="OnDisconnectingAsync"/>, and disposing of internal resources.
    /// </summary>
    public virtual async Task DisconnectAsync()
    {
        if (_panelCts is not null)
        {
            try
            {
                if (!_panelCts.IsCancellationRequested)
                    await _panelCts.CancelAsync();
            }
            catch (ObjectDisposedException) { }

            await OnDisconnectingAsync();

            _sequentialWorkQueue.Writer.TryComplete();

            if (_workTask is not null)
            {
                try { await _workTask; }
                catch (OperationCanceledException) { }
            }

            try { _panelCts.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Asynchronously disposes the panel by delegating to <see cref="DisconnectAsync"/>.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        GC.SuppressFinalize(this);
    }
}
