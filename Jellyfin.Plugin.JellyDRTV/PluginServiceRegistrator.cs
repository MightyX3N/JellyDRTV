using Jellyfin.Plugin.JellyDRTV.Download;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyDRTV;

/// <summary>Registers the plugin's services with Jellyfin.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<DrtvService>();
        serviceCollection.AddHostedService<DownloadWorker>();
    }
}
