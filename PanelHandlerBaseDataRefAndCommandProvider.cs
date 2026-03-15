using nocscienceat.XPlanePanel.Services;

namespace nocscienceat.XPlanePanel;

public abstract partial class PanelHandlerBase<TConfig>
{
    private Dictionary<string, string> DataRefs { get; set; } = new();
    private Dictionary<string, string> Commands { get; set; } = new();

    public void RegisterCommands(IEnumerable<(string, string)> commands)
    {
        foreach ((string key, string path) in commands)
        {
            Commands[key] = path;
        }
    }

    public void RegisterDataRefs(IEnumerable<(string, string)> dataRefs)
    {
        foreach ((string key, string path) in dataRefs)
        {
            DataRefs[key] = path;
        }
    }

    public string GetCommand(string key)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetCommand(key, this,  out var overridePath))
            return overridePath;

        if (Commands.TryGetValue(key, out var commandPath))
            return commandPath;

        throw new KeyNotFoundException($"Command '{key}' not found in configuration");
    }

    public bool TryGetCommand(string key, out string commandPath)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetCommand(key, this, out commandPath!))
            return true;

        return Commands.TryGetValue(key, out commandPath!);
    }

    public string GetDataRefPath(string key)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetDataRefPath(key, this, out var overridePath))
            return overridePath;

        if (DataRefs.TryGetValue(key, out var dataRefPath))
            return dataRefPath;

        throw new KeyNotFoundException($"DataRefPath '{key}' not found in configuration");
    }

    public bool TryGetDataRefPath(string key, out string dataRefPath)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetDataRefPath(key, this, out dataRefPath!))
            return true;

        return DataRefs.TryGetValue(key, out dataRefPath!);
    }
}