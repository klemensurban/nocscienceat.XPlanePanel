namespace nocscienceat.XPlanePanel.Services;

/// <summary>
/// Singleton service that holds cross-panel dataref and command overrides.
/// Checked by <see cref="PanelHandlerBase{TConfig}"/> before falling back
/// to the panel's own registered mappings.
/// </summary>
public interface IDataRefCommandProvider
{
    /// <summary>
    /// Tries to get an overridden dataref path for the given logical key.
    /// </summary>
    bool TryGetDataRefPath(string key, IPanelHandler panelHandler, out string dataRefPath);

    /// <summary>
    /// Tries to get an overridden command path for the given logical key.
    /// </summary>
    bool TryGetCommand(string key, IPanelHandler panelHandler, out string commandPath);

    /// <summary>
    /// Sets or removes a dataref override. Pass <c>null</c> to clear.
    /// </summary>
}
