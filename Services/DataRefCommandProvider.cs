using Microsoft.Extensions.Configuration;

namespace nocscienceat.XPlanePanel.Services;

/// <summary>
/// Singleton that stores cross-panel dataref and command overrides.
/// Populated once at startup; read-only at runtime.
/// </summary>
/// 
public class DataRefCommandProvider : IDataRefCommandProvider
{
    private readonly Dictionary<string, string> _dataRefs = new();
    private readonly Dictionary<string, string> _commands = new();

    public DataRefCommandProvider(IConfiguration configuration)
    {
        foreach (var panel in configuration.GetSection("XplaneDataRefsCommands").GetChildren())
        {
            string panelName = panel.Key;

            foreach (var entry in panel.GetSection("DataRefs").GetChildren())
            {
                if (entry.Value is not null)
                    _dataRefs[$"{panelName}:{entry.Key}"] = entry.Value;
            }

            foreach (var entry in panel.GetSection("Commands").GetChildren())
            {
                if (entry.Value is not null)
                    _commands[$"{panelName}:{entry.Key}"] = entry.Value;
            }
        }
    }

    public bool TryGetDataRefPath(string key, IPanelHandler panelHandler, out string dataRefPath)
    {
        return _dataRefs.TryGetValue($"{panelHandler.PanelName}:{key}", out dataRefPath!);
    }

    public bool TryGetCommand(string key, IPanelHandler panelHandler, out string commandPath)
    {
        return _commands.TryGetValue($"{panelHandler.PanelName}:{key}", out commandPath!);
    }
}
