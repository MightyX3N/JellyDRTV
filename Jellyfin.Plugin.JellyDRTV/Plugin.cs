using System.Globalization;
using Jellyfin.Plugin.JellyDRTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyDRTV;

/// <summary>
/// JellyDRTV: DRTV's catalog as a Jellyfin library, with streaming and downloads.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "6f1d2c3a-8b4e-4d7a-9c21-5e0a7b3d9f14";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public override string Name => "JellyDRTV";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "DRTV's catalog as a Jellyfin library: stream everything, download what you want, follow series.";

    public static Plugin? Instance { get; private set; }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
