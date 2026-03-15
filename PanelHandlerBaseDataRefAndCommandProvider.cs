using nocscienceat.XPlanePanel.Services;

namespace nocscienceat.XPlanePanel;

public abstract partial class PanelHandlerBase<TConfig>
{
    private Dictionary<string, string> DataRefs { get; set; } = new();
    private Dictionary<string, string> Commands { get; set; } = new();

    /// <summary>
    /// Registers a collection of commands by associating each key with a command path.
    /// </summary>
    /// <param name="commands">A collection of tuples where each tuple contains a key and a command path string.</param>
    public void RegisterCommands(IEnumerable<(string, string)> commands)
    {
        foreach ((string key, string path) in commands)
        {
            Commands[key] = path;
        }
    }

    /// <summary>
    /// Registers a collection of dataref subscriptions by associating each key with a dataref path.
    /// </summary>
    /// <param name="dataRefs">A collection of tuples where each tuple contains a key and a dataref path string.</param>
    public void RegisterDataRefs(IEnumerable<(string, string)> dataRefs)
    {
        foreach ((string key, string path) in dataRefs)
        {
            DataRefs[key] = path;
        }
    }

    /// <summary>
    /// Gets the command path associated with the specified key.
    /// If an override provider is configured and supplies a value for this key, that value is returned instead.
    /// </summary>
    /// <param name="key">The key identifying the command.</param>
    /// <returns>The command path string.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the key is not found in the registered commands or override provider.</exception>
    public string GetCommand(string key)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetCommand(key, this,  out var overridePath))
            return overridePath;

        if (Commands.TryGetValue(key, out var commandPath))
            return commandPath;

        throw new KeyNotFoundException($"Command '{key}' not found in configuration");
    }

    /// <summary>
    /// Attempts to get the command path associated with the specified key.
    /// If an override provider is configured and supplies a value for this key, that value is returned instead.
    /// </summary>
    /// <param name="key">The key identifying the command.</param>
    /// <param name="commandPath">When this method returns, contains the command path if the key was found; otherwise, an undefined value.</param>
    /// <returns><see langword="true"/> if the command path was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGetCommand(string key, out string commandPath)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetCommand(key, this, out commandPath!))
            return true;

        return Commands.TryGetValue(key, out commandPath!);
    }

    /// <summary>
    /// Gets the dataref path associated with the specified key.
    /// If an override provider is configured and supplies a value for this key, that value is returned instead.
    /// </summary>
    /// <param name="key">The key identifying the dataref.</param>
    /// <returns>The dataref path string.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the key is not found in the registered datarefs or override provider.</exception>
    public string GetDataRefPath(string key)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetDataRefPath(key, this, out var overridePath))
            return overridePath;

        if (DataRefs.TryGetValue(key, out var dataRefPath))
            return dataRefPath;

        throw new KeyNotFoundException($"DataRefPath '{key}' not found in configuration");
    }

    /// <summary>
    /// Attempts to get the dataref path associated with the specified key.
    /// If an override provider is configured and supplies a value for this key, that value is returned instead.
    /// </summary>
    /// <param name="key">The key identifying the dataref.</param>
    /// <param name="dataRefPath">When this method returns, contains the dataref path if the key was found; otherwise, an undefined value.</param>
    /// <returns><see langword="true"/> if the dataref path was found; otherwise, <see langword="false"/>.</returns>
    public bool TryGetDataRefPath(string key, out string dataRefPath)
    {
        if (_overrideProvider is not null && _overrideProvider.TryGetDataRefPath(key, this, out dataRefPath!))
            return true;

        return DataRefs.TryGetValue(key, out dataRefPath!);
    }
}