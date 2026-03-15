using Microsoft.Extensions.DependencyInjection;
using nocscienceat.XPlanePanel.Services;

namespace nocscienceat.XPlanePanel;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the panel infrastructure: <see cref="DataRefCommandProvider"/> singleton
    /// and <see cref="PanelHostedService"/> that orchestrates panel lifecycle.
    /// </summary>
    public static IServiceCollection AddXPlanePanel(this IServiceCollection services)
    {
        services.AddSingleton<IDataRefCommandProvider, DataRefCommandProvider>();
        services.AddHostedService<PanelHostedService>();
        return services;
    }
}
