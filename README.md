# nocscienceat.XPlanePanel

A .NET 10 library for building X-Plane cockpit panel modules. Provides a single-threaded work queue, dataref subscription helpers, lifecycle management, and an optional centralized dataref/command mapping service — so panel implementations can focus purely on hardware-specific logic.

Depends on `nocscienceat.XPlaneWebConnector` for X-Plane communication.

---

## Table of Contents

1. [Quick Start](#1-quick-start)
2. [Class Hierarchy](#2-class-hierarchy)
3. [What PanelHandlerBase Provides](#3-what-panelhandlerbase-provides)
   - 3.1 [Single-Threaded Work Queue](#31-single-threaded-work-queue)
   - 3.2 [EnqueueWork — Thread-Safe Work Scheduling](#32-enqueuework--thread-safe-work-scheduling)
   - 3.3 [SubscribeEnqueuedAsync — Dataref Subscription Helpers](#33-subscribeenqueuedasync--dataref-subscription-helpers)
   - 3.4 [Lifecycle Management](#34-lifecycle-management)
   - 3.5 [Protected Fields Available to Subclasses](#35-protected-fields-available-to-subclasses)
   - 3.6 [Configuration](#36-configuration)
4. [Dataref & Command Resolution](#4-dataref--command-resolution)
   - 4.1 [Using Raw Strings (Simplest)](#41-using-raw-strings-simplest)
   - 4.2 [Per-Panel Registration with RegisterDataRefs / RegisterCommands](#42-per-panel-registration-with-registerdatarefs--registercommands)
   - 4.3 [Centralized Overrides with DataRefCommandProvider](#43-centralized-overrides-with-datarefcommandprovider)
   - 4.4 [Lookup Order](#44-lookup-order)
5. [Implementing a Panel Module](#5-implementing-a-panel-module)
   - 5.1 [Required: Abstract Members to Implement](#51-required-abstract-members-to-implement)
   - 5.2 [Optional: Virtual Members to Override](#52-optional-virtual-members-to-override)
   - 5.3 [Complete Example: A Minimal Panel from Scratch](#53-complete-example-a-minimal-panel-from-scratch)
6. [Lifecycle Sequence Diagrams](#6-lifecycle-sequence-diagrams)
   - 6.1 [Startup](#61-startup)
   - 6.2 [Shutdown](#62-shutdown)
7. [Threading Model & Safety Guarantees](#7-threading-model--safety-guarantees)
8. [Registration & Wiring](#8-registration--wiring)
9. [Intermediate Base Classes](#9-intermediate-base-classes)

---

## 1. Quick Start

**Program.cs** — three lines to set up the entire panel infrastructure:

```csharp
builder.Services.AddXPlaneWebConnector(builder.Configuration);
builder.Services.AddXPlanePanel();                                  // registers DataRefCommandProvider + PanelHostedService
builder.Services.AddSingleton<IPanelHandler, MyPanelHandler>();     // register your panel(s)
```

**appsettings.json** — enable your panel and set connection options:

```json
{
  "Panels": {
    "MIP": {
      "Enabled": true,
      "ListenPort": 9000
    }
  }
}
```

**datarefs.json** (optional) — centralized dataref/command mappings:

```json
{
  "XplaneDataRefsCommands": {
    "MIP": {
      "DataRefs": {
        "Heading": "sim/cockpit2/gauges/heading_deg"
      },
      "Commands": {
        "HeadingUp": "sim/autopilot/heading_up"
      }
    }
  }
}
```

That's it. `PanelHostedService` discovers all registered `IPanelHandler` instances, waits for X-Plane, and calls `ConnectAsync` / `DisconnectAsync` automatically.

---

## 2. Class Hierarchy

```
IPanelHandler                              interface (IAsyncDisposable)
?   PanelName: string
?   IsConnected: bool
?   ConnectAsync(CancellationToken): Task
?   DisconnectAsync(): Task
?
???? PanelHandlerBase<TConfig>            abstract class
     ?   where TConfig : PanelConfig, new()
     ?   Provides: work queue, EnqueueWork, SubscribeEnqueuedAsync,
     ?             lifecycle template, connector/logger/config,
     ?             dataref/command registration & lookup
     ?   _config is typed as TConfig
     ?
     ???? PanelHandlerBase<UdpPanelConfig> direct subclass with custom config
     ?       implements: PanelName, OnConnectedAsync
     ?
     ???? JavaSimulatorPanelHandlerBase   intermediate base (serial UART)
          ?   = PanelHandlerBase<SerialPanelConfig>
          ?   Adds: SerialPort, SendToHardware, receive buffer,
          ?         "command,value;" framing, ProcessCommandAsync
          ?
          ???? OvhPanelHandler            concrete panel (in consumer project)
```

> **You do not need an intermediate base class.** If your panel is simple enough,
> you can inherit directly from `PanelHandlerBase<YourConfig>`.

---

## 3. What PanelHandlerBase Provides

### 3.1 Single-Threaded Work Queue

The core design principle: **all panel state is accessed from exactly one thread.**

```
                    +--------------------------------------------+
                    |          _workQueue (unbounded)            |
                    |     Channel<Func<Task>>, SingleReader      |
                    +--------------------------------------------+
                                       |
                                       v
                    +--------------------------------------------+
                    |       DrainWorkQueueAsync (single task)    |
                    |                                            |
                    |  await foreach (var work in Reader)        |
                    |  {                                         |
                    |      await work();   // sequential         |
                    |  }                                         |
                    +--------------------------------------------+
```

Work items are enqueued from **any thread** (connector callbacks, hardware receive events, external callers) but always **execute sequentially** on the single drain task. This eliminates the need for `lock`, `volatile`, or `Interlocked` on panel state fields.

The work queue task is started automatically by `ConnectAsync` and drained during `DisconnectAsync`. You never need to manage it yourself.

### 3.2 EnqueueWork — Thread-Safe Work Scheduling

Two overloads for scheduling work onto the panel task:

```csharp
// Async work item
protected void EnqueueWork(Func<Task> work);

// Sync work item (wrapped internally)
protected void EnqueueWork(Action work);
```

Both are **non-blocking** — they write to the channel and return immediately. The actual execution happens later on the drain task.

**When to use:** Whenever you receive data from a source that runs on a different thread (hardware events, timer callbacks, external API calls) and need to touch panel state.

```csharp
// Example: a hypothetical USB HID receive callback
void OnHidReport(HidReport report)
{
    EnqueueWork(() =>
    {
        // Safe: runs on the single panel task
        _brightness = report.Data[0] / 255f;
        UpdateDisplay();
    });
}
```

### 3.3 SubscribeEnqueuedAsync — Dataref Subscription Helpers

Convenience methods that subscribe to an X-Plane dataref via `_connector.SubscribeAsync` **and** automatically route the callback through the work queue. This means the callback body can safely read/write panel state without any synchronization.

All four X-Plane dataref types are covered, each with a synchronous (`Action<T>`) and asynchronous (`Func<T, Task>`) variant:

| Dataref type | Sync variant | Async variant |
|---|---|---|
| `double` | `SubscribeEnqueuedAsync(path, Action<double>)` | `SubscribeEnqueuedAsync(path, Func<double, Task>)` |
| `float` | `SubscribeEnqueuedAsync(path, Action<float>)` | `SubscribeEnqueuedAsync(path, Func<float, Task>)` |
| `int` | `SubscribeEnqueuedAsync(path, Action<int>)` | `SubscribeEnqueuedAsync(path, Func<int, Task>)` |
| `string` | `SubscribeEnqueuedAsync(path, Action<string>)` | `SubscribeEnqueuedAsync(path, Func<string, Task>)` |

**Usage:**

```csharp
// Sync — just update state (most common)
await SubscribeEnqueuedAsync("sim/cockpit/autopilot/heading", (float h) => _heading = h);

// Async — when the callback itself needs to await something
await SubscribeEnqueuedAsync("sim/aircraft/view/acf_tailnum", async (string tail) =>
{
    _tailNumber = tail;
    await ReloadAircraftProfileAsync(tail);
});
```

**Note:** If you don't need work-queue routing (e.g. the callback only calls `SendToHardware` which has its own queue), you can call `_connector.SubscribeAsync(...)` directly instead.

### 3.4 Lifecycle Management

`PanelHandlerBase` implements the full `IPanelHandler` lifecycle as a **template method pattern** with hooks for subclasses:

| Method | Role | Subclass action |
|---|---|---|
| `ConnectAsync(ct)` | Public entry point. Checks `_config.Enabled`, calls `RegisterDataRefsAndCommands`, creates CTS, starts work task, calls `OnConnectedAsync`. | Usually don't override. |
| `OnConnectedAsync(ct)` | **Abstract hook.** Called after the work queue is running. | **Must implement.** Open your channel, subscribe to datarefs. |
| `DisconnectAsync()` | Public entry point. Cancels CTS, calls `OnDisconnectingAsync`, drains work queue. | Usually don't override. |
| `OnDisconnectingAsync()` | **Virtual hook.** Called after cancellation, before the work queue is drained. | Override to send shutdown messages, close channels. |
| `DisposeAsync()` | Calls `DisconnectAsync()`, suppresses finalizer. | Override if you own additional disposable resources. |

### 3.5 Protected Fields Available to Subclasses

| Field | Type | Description |
|---|---|---|
| `_connector` | `IXPlaneWebConnector` | The X-Plane connector. Use for subscriptions, `SetDataRefValueAsync`, `SendCommandAsync`. |
| `_logger` | `ILogger` | Logger instance. Use `_logger.LogDebug(...)`, `_logger.LogInformation(...)`, etc. |
| `_config` | `TConfig` | The panel's typed configuration, bound from `Panels:{PanelName}` in `appsettings.json`. The concrete type depends on which base class you inherit: `PanelConfig` (just `Enabled`), `SerialPanelConfig` (adds `PortName`, `BaudRate`), or your own POCO. |

### 3.6 Configuration

Configuration is **panel-specific and type-safe**. The base class is generic on `TConfig` and binds the `Panels:{PanelName}` section from `IConfiguration` directly — no wrapper dictionary needed.

#### Config class hierarchy

```csharp
public class PanelConfig                   // universal — just the on/off switch
{
    public bool Enabled { get; set; }
}

public class SerialPanelConfig : PanelConfig   // for serial (UART) panels
{
    public string PortName { get; set; } = "disable";
    public int BaudRate { get; set; } = 19200;
}

// You can define your own:
public class UdpPanelConfig : PanelConfig      // for a hypothetical UDP panel
{
    public int ListenPort { get; set; } = 9000;
    public string RemoteHost { get; set; } = "127.0.0.1";
}
```

#### JSON structure (`appsettings.json`)

Flat — each panel is a direct child of `Panels`, keyed by `PanelName`:

```json
{
  "Panels": {
    "OVH": {
      "Enabled": true,
      "PortName": "COM7",
      "BaudRate": 19200
    },
    "MIP": {
      "Enabled": true,
      "ListenPort": 9000,
      "RemoteHost": "192.168.1.50"
    }
  }
}
```

If no entry exists for a panel's name, the config defaults to `new TConfig()` (which has `Enabled = false`). No `Configure<>` call is needed in `Program.cs` — panels read `IConfiguration` directly.

---

## 4. Dataref & Command Resolution

There are three ways to reference X-Plane dataref paths and command paths in your panel code, from simplest to most structured. They can be mixed freely.

### 4.1 Using Raw Strings (Simplest)

Pass X-Plane paths directly — no registration needed:

```csharp
await SubscribeEnqueuedAsync("sim/cockpit2/gauges/heading_deg", (float h) => _heading = h);
await _connector.SendCommandAsync("sim/autopilot/heading_up");
```

This is perfectly fine for panels with a small number of datarefs or when you don't need runtime overrides.

### 4.2 Per-Panel Registration with RegisterDataRefs / RegisterCommands

Override `RegisterDataRefsAndCommands` to register logical names that map to X-Plane paths. Then use `GetDataRefPath(key)` and `GetCommand(key)` to look them up:

```csharp
protected override void RegisterDataRefsAndCommands()
{
    RegisterDataRefs(new (string, string)[]
    {
        ("Heading", "sim/cockpit2/gauges/heading_deg"),
        ("BAT1_V", "AirbusFBW/BatVolts[0]")
    });

    RegisterCommands(new (string, string)[]
    {
        ("HeadingUp", "sim/autopilot/heading_up")
    });
}
```

Then in `OnConnectedAsync`:

```csharp
await SubscribeEnqueuedAsync(GetDataRefPath("Heading"), (float h) => _heading = h);
await _connector.SendCommandAsync(GetCommand("HeadingUp"));
```

`RegisterDataRefsAndCommands` is called automatically by `ConnectAsync` before `OnConnectedAsync`, so the dictionaries are populated before your subscriptions run.

Available lookup methods:

| Method | Returns | On missing key |
|---|---|---|
| `GetDataRefPath(key)` | `string` | Throws `KeyNotFoundException` |
| `TryGetDataRefPath(key, out path)` | `bool` | Returns `false` |
| `GetCommand(key)` | `string` | Throws `KeyNotFoundException` |
| `TryGetCommand(key, out path)` | `bool` | Returns `false` |

### 4.3 Centralized Overrides with DataRefCommandProvider

The `DataRefCommandProvider` is a singleton that loads dataref and command mappings from `IConfiguration` (typically a `datarefs.json` file). It acts as an **upstream override layer** — when a mapping exists in the provider, it takes priority over the panel's own registered mappings.

This is useful for:
- Keeping all X-Plane paths in a single JSON file, separate from code
- Switching aircraft profiles without recompiling
- Overriding individual mappings at deployment time

#### JSON structure (`datarefs.json`)

Organized by panel name, then `DataRefs` and `Commands`:

```json
{
  "XplaneDataRefsCommands": {
    "OVH": {
      "DataRefs": {
        "BAT1_V": "AirbusFBW/BatVolts[0]",
        "LightDome": "AirbusFBW/OHPLightSwitches[8]"
      },
      "Commands": {
        "ApuStart": "toliss_airbus/apucommands/StarterToggle"
      }
    },
    "MIP": {
      "DataRefs": {
        "Heading": "sim/cockpit2/gauges/heading_deg"
      }
    }
  }
}
```

Internally, these are stored as compound keys: `"OVH:BAT1_V"`, `"OVH:ApuStart"`, `"MIP:Heading"`. The provider scopes lookups by `panelHandler.PanelName`, so different panels can use the same logical key for different X-Plane paths.

#### Setup

Add `datarefs.json` to configuration sources (optional — the provider is fail-safe if the file or section is missing):

```csharp
builder.Configuration.AddJsonFile("datarefs.json", optional: true, reloadOnChange: true);
```

`AddXPlanePanel()` registers the provider automatically. No additional wiring needed.

#### Fail-safe behavior

- If `datarefs.json` is missing ? provider has zero entries, all lookups fall through to panel-local dictionaries
- If `"XplaneDataRefsCommands"` section is missing ? same behavior
- If a panel section or individual entry is missing ? only that lookup falls through
- If an entry has a `null` value ? it is silently skipped

### 4.4 Lookup Order

When panel code calls `GetDataRefPath("BAT1_V")` or `GetCommand("ApuStart")`:

```
1. DataRefCommandProvider  ?  check "OVH:BAT1_V" in centralized mappings
                               (if registered and entry exists, return it)
                                         |
                                   not found
                                         ?
2. Panel-local dictionary  ?  check "BAT1_V" in RegisterDataRefs entries
                               (if registered, return it)
                                         |
                                   not found
                                         ?
3. KeyNotFoundException     ?  thrown (or false for TryGet* variants)
```

The centralized provider always wins. This lets you ship default mappings in code and override specific entries via JSON at deployment time.

---

## 5. Implementing a Panel Module

### 5.1 Required: Abstract Members to Implement

There are exactly **two** abstract members you must implement:

---

#### `PanelName` (property)

```csharp
public abstract string PanelName { get; }
```

A unique identifier for this panel. Used for:
- Binding configuration from `Panels:{PanelName}` in `appsettings.json`
- Scoping `DataRefCommandProvider` lookups to this panel
- Log messages (`"{Panel} connected"`, etc.)

```csharp
public override string PanelName => "MIP";
```

---

#### `OnConnectedAsync(CancellationToken)` (method)

```csharp
protected abstract Task OnConnectedAsync(CancellationToken cancellationToken);
```

Called **once** after `RegisterDataRefsAndCommands` has run and the work queue is running. This is where you:

1. **Open your communication channel** (serial port, USB device, network socket, …)
2. **Start any additional background tasks** you need (receive loops, write pumps)
3. **Subscribe to X-Plane datarefs** that your panel needs

The `cancellationToken` is a linked token that will be cancelled when `DisconnectAsync` is called. Use it for all long-running operations.

**Cache warm-up:** `SubscribeAsync` / `SubscribeEnqueuedAsync` resolve dataref IDs as a side effect, so subscribed datarefs are already cached. Datarefs only used with `SetDataRefValueAsync` and commands used with `SendCommandAsync` are resolved lazily on first use. To avoid a REST round-trip on the first button press, pre-resolve them at the end of `OnConnectedAsync`:

```csharp
// Pre-resolve write-only datarefs and all commands
await Task.WhenAll(
    _connector.PreResolveDataRefAsync("sim/cockpit/lights/landing_lights_on"),
    _connector.PreResolveCommandAsync("sim/autopilot/heading_up")
);
```

---

### 5.2 Optional: Virtual Members to Override

These have default implementations but you may want to override them:

---

#### `RegisterDataRefsAndCommands()` (method)

```csharp
protected virtual void RegisterDataRefsAndCommands() { }
```

Override to register logical dataref/command names. Called automatically by `ConnectAsync` before `OnConnectedAsync`. See [Section 4.2](#42-per-panel-registration-with-registerdatarefs--registercommands).

---

#### `OnDisconnectingAsync()` (method)

```csharp
protected virtual Task OnDisconnectingAsync() => Task.CompletedTask;
```

Called during `DisconnectAsync`, **after** the CancellationToken is cancelled but **before** the work queue is drained and completed. This is the place to:

- Send a "shutdown" or "lights off" message to hardware
- Close your communication channel
- Await any background tasks you started in `OnConnectedAsync`

**Important:** After this method returns, the base class will complete and drain the work queue, then dispose the CancellationTokenSource.

---

#### `IsConnected` (property)

```csharp
public virtual bool IsConnected => false;
```

Override to report whether your hardware channel is currently open.

```csharp
public override bool IsConnected => _myDevice?.IsOpen ?? false;
```

---

#### `ConnectAsync(CancellationToken)` / `DisconnectAsync()` / `DisposeAsync()`

You **rarely** need to override these. The defaults handle the enabled check, CTS management, work task lifecycle, and hook invocations. Override only if you need to change the enabled check logic or need additional cleanup after the work queue is fully drained.

---

### 5.3 Complete Example: A Minimal Panel from Scratch

This example implements a hypothetical panel that communicates over a UDP socket — no intermediate base class, directly on `PanelHandlerBase<TConfig>` with its own config POCO:

```csharp
using nocscienceat.XPlanePanel;
using nocscienceat.XPlanePanel.Configuration;

// Panel-specific configuration POCO
public class UdpPanelConfig : PanelConfig
{
    public int ListenPort { get; set; } = 9000;
    public string RemoteHost { get; set; } = "127.0.0.1";
}

public class MipPanelHandler : PanelHandlerBase<UdpPanelConfig>
{
    private UdpClient? _udp;
    private Task? _receiveTask;

    // Panel state — safe to access without locks because
    // all access is from the single work queue task.
    private float _heading;
    private int _gearPosition;

    public override string PanelName => "MIP";
    public override bool IsConnected => _udp != null;

    public MipPanelHandler(
        IXPlaneWebConnector connector,
        IConfiguration configuration,
        ILogger<MipPanelHandler> logger,
        IDataRefCommandProvider? overrideProvider = null)
        : base(connector, configuration, logger, overrideProvider)
    {
    }

    protected override async Task OnConnectedAsync(CancellationToken cancellationToken)
    {
        // 1. Open communication channel — _config is UdpPanelConfig
        _udp = new UdpClient(_config.ListenPort);
        _logger.LogInformation("{Panel} listening on UDP port {Port}", PanelName, _config.ListenPort);

        // 2. Start receive loop
        _receiveTask = Task.Run(() => ReceiveLoopAsync(cancellationToken), cancellationToken);

        // 3. Subscribe to X-Plane datarefs (raw strings — simplest approach)
        await SubscribeEnqueuedAsync("sim/cockpit2/gauges/heading_deg", (float h) =>
        {
            _heading = h;
            SendUdp($"HDG:{h:F1}");
        });

        await SubscribeEnqueuedAsync("sim/cockpit2/controls/gear_handle_down", (int g) =>
        {
            _gearPosition = g;
            SendUdp($"GEAR:{g}");
        });
    }

    // ... (receive loop, command handling, SendUdp, etc.)
}
```

**Registration in `Program.cs`:**

```csharp
builder.Services.AddSingleton<IPanelHandler, MipPanelHandler>();
```

That's it — `PanelHostedService` will discover it via `IEnumerable<IPanelHandler>` and call `ConnectAsync` / `DisconnectAsync` automatically.

---

## 6. Lifecycle Sequence Diagrams

### 6.1 Startup

```
PanelHostedService         PanelHandlerBase              YourPanel
       |                         |                           |
       |  ConnectAsync(ct)       |                           |
       |------------------------>|                           |
       |                         |                           |
       |                         |  _config.Enabled?         |
       |                         |  +- No -> log, return     |
       |                         |  +- Yes v                 |
       |                         |                           |
       |                         |  RegisterDataRefsAndCmds  |
       |                         |-------------------------->|
       |                         |  (populate dictionaries)  |
       |                         |<--------------------------|
       |                         |                           |
       |                         |  Create _panelCts         |
       |                         |  Start _workTask          |
       |                         |    (DrainWorkQueueAsync)  |
       |                         |                           |
       |                         |  OnConnectedAsync(ct)     |
       |                         |-------------------------->|
       |                         |                           |
       |                         |      1. Open channel      |
       |                         |      2. Start recv loop   |
       |                         |      3. Subscribe datarefs|
       |                         |                           |
       |                         |<--------------------------|
       |<------------------------|                           |
       |                         |                           |
```

### 6.2 Shutdown

```
PanelHostedService         PanelHandlerBase              YourPanel
       |                         |                           |
       |  DisconnectAsync()      |                           |
       |------------------------>|                           |
       |                         |                           |
       |                         |  Cancel _panelCts         |
       |                         |                           |
       |                         |  OnDisconnectingAsync()   |
       |                         |-------------------------->|
       |                         |                           |
       |                         |      1. Await recv task   |
       |                         |      2. Close channel     |
       |                         |      3. Dispose resources |
       |                         |                           |
       |                         |<--------------------------|
       |                         |                           |
       |                         |  Complete _workQueue      |
       |                         |  Await _workTask          |
       |                         |  Dispose _panelCts        |
       |                         |                           |
       |<------------------------|                           |
       |                         |                           |
```

---

## 7. Threading Model & Safety Guarantees

```
+----------------------------------------------------------------------+
|                          Threading Overview                          |
+----------------------------------------------------------------------+
|                                                                      |
|  ANY THREAD                        PANEL WORK TASK (single)          |
|  ----------                        -------------------------         |
|                                                                      |
|  * Hardware receive events    -->  EnqueueWork(...)  -->  work()     |
|  * Connector callbacks        -->  EnqueueWork(...)  -->  work()     |
|  * SubscribeEnqueuedAsync     -->  EnqueueWork(...)  -->  work()     |
|  * Timer callbacks            -->  EnqueueWork(...)  -->  work()     |
|                                                                      |
|  Panel state fields (_heading, _gearPosition, _brightness, ...)      |
|  are ONLY read/written from the work task. No locks needed.          |
|                                                                      |
|  Calls to _connector (SetDataRefValueAsync, SendCommandAsync)        |
|  are thread-safe and can be called from the work task.               |
|                                                                      |
+----------------------------------------------------------------------+
```

**Rules for panel implementors:**

1. **All mutable panel state** (toggle flags, cached values, display state) must only be accessed from callbacks that run on the work queue task — i.e., inside `EnqueueWork(...)` or inside `SubscribeEnqueuedAsync` callbacks.
2. **Sending to hardware** can happen from the work task. If your hardware channel is not thread-safe, add your own write queue (see `JavaSimulatorPanelHandlerBase._serialWriteQueue` for an example).
3. **`_connector` method calls** (`SetDataRefValueAsync`, `SendCommandAsync`, `SubscribeAsync`) are thread-safe and can be called from anywhere, including the work task.
4. **Don't block the work task** for long periods. If you have expensive I/O (e.g. a slow serial write), offload it to a separate queue/task.

---

## 8. Registration & Wiring

Every panel module must be registered in `Program.cs` as an `IPanelHandler`:

```csharp
builder.Services.AddXPlanePanel();                                  // infrastructure (once)
builder.Services.AddSingleton<IPanelHandler, MipPanelHandler>();    // your panel(s)
builder.Services.AddSingleton<IPanelHandler, OvhPanelHandler>();
```

No `Configure<>` call is needed — each panel reads its own config section from `IConfiguration` directly (which is auto-registered by `Host.CreateApplicationBuilder`).

`AddXPlanePanel()` registers:
- `IDataRefCommandProvider` ? `DataRefCommandProvider` (singleton, reads `XplaneDataRefsCommands` from `IConfiguration`)
- `PanelHostedService` (hosted service that orchestrates panel lifecycle)

The `PanelHostedService` will:

1. Wait for X-Plane to be available
2. Start the connector (WebSocket)
3. Call `ConnectAsync` on **all** registered `IPanelHandler` instances
4. On shutdown, call `DisconnectAsync` on all panels, then stop the connector

No manual orchestration needed — just register and the hosted service handles the rest.

---

## 9. Intermediate Base Classes

For panel families that share a communication protocol, you can introduce an **intermediate base class** between `PanelHandlerBase` and the concrete panel. This library includes one:

### `JavaSimulatorPanelHandlerBase`

Closes the generic as `PanelHandlerBase<SerialPanelConfig>`, adding serial UART communication with `"command,value;"` framing for JavaSimulator hardware panels. All serial panels share `SerialPanelConfig` (which adds `PortName` and `BaudRate` on top of `PanelConfig.Enabled`):

| Added by intermediate class | Description |
|---|---|
| `SerialPort` lifecycle | Open, DTR/RTS, baud rate, close |
| `SendToHardware(cmd, val)` | Non-blocking enqueue to serial write pump |
| `_serialWriteQueue` | Dedicated serial write background task |
| Receive buffer + delimiter parsing | Assembles `"cmd,val;"` frames from byte stream |
| `ProcessCommandAsync(cmd, val)` | Abstract — concrete panels implement command dispatch |
| `SubscribeToDataRefsAsync(ct)` | Abstract — concrete panels subscribe to their datarefs |
| `"DOFF,1;"` shutdown | Sent to hardware on disconnect |

**When to create an intermediate base class:**

- Multiple panels share the **same communication channel type and framing protocol**
- The shared code is **substantial** (not just a few lines)
- You want concrete panels to focus purely on **panel-specific logic** (which datarefs, which commands, which LEDs)

**When NOT to bother:**

- Only one panel uses that protocol
- The communication code is trivial enough to inline in the concrete panel

---

## Quick Reference: What to Implement

| Member | Kind | Required? | Your action |
|---|---|---|---|
| `PanelName` | `abstract property` | **Yes** | Return your panel's unique name (e.g. `"MIP"`) |
| `OnConnectedAsync(ct)` | `abstract method` | **Yes** | Open channel, start tasks, subscribe to datarefs |
| `RegisterDataRefsAndCommands()` | `virtual method` | Optional | Register logical names for datarefs/commands |
| `OnDisconnectingAsync()` | `virtual method` | Recommended | Close channel, await tasks, dispose resources |
| `IsConnected` | `virtual property` | Recommended | Return whether your channel is open |
| `ConnectAsync(ct)` | `virtual method` | Rarely | Only if you need to change the enabled check |
| `DisconnectAsync()` | `virtual method` | Rarely | Only if you need cleanup after work queue drain |
| `DisposeAsync()` | `virtual method` | If needed | Only if you own additional disposable resources |
