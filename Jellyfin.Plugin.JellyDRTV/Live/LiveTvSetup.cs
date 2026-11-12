using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Live;

/// <summary>
/// Adds (or removes) the DRTV tuner and guide in Jellyfin's Live TV settings, pointing at the plugin's own endpoints.
/// </summary>
public class LiveTvSetup
{
    private const string Marker = "/JellyDRTV/LiveTv/";
    private readonly IConfigurationManager _config;
    private readonly ITunerHostManager _tuners;
    private readonly IListingsManager _listings;
    private readonly ITaskManager _tasks;
    private readonly ILogger _logger;

    public LiveTvSetup(IConfigurationManager config, ITunerHostManager tuners, IListingsManager listings, ITaskManager tasks, ILogger logger)
    {
        _config = config;
        _tuners = tuners;
        _listings = listings;
        _tasks = tasks;
        _logger = logger;
    }

    public static string M3uUrl(string serverUrl) => serverUrl + Marker + "channels.m3u";

    public static string GuideUrl(string serverUrl) => serverUrl + Marker + "guide.xml";

    /// <summary>Makes Jellyfin's Live TV settings match the plugin setting. Safe to call repeatedly.</summary>
    public async Task ApplyAsync(bool enabled, string serverUrl)
    {
        var options = _config.GetConfiguration<LiveTvOptions>("livetv");
        var ourTuners = options.TunerHosts.Where(t => t.Url?.Contains(Marker, StringComparison.Ordinal) == true).ToList();
        var ourGuides = options.ListingProviders.Where(l => l.Path?.Contains(Marker, StringComparison.Ordinal) == true).ToList();

        if (!enabled)
        {
            foreach (var t in ourTuners)
            {
                _tuners.DeleteTunerHost(t.Id);
            }

            foreach (var l in ourGuides)
            {
                _listings.DeleteListingsProvider(l.Id);
            }

            if (ourTuners.Count + ourGuides.Count > 0)
            {
                _logger.LogInformation("JellyDRTV: removed DRTV from Live TV");
            }

            return;
        }

        var m3u = M3uUrl(serverUrl);
        var guide = GuideUrl(serverUrl);
        var changed = false;

        // Keep exactly one tuner and one guide, with the current address.
        foreach (var extra in ourTuners.Skip(1))
        {
            _tuners.DeleteTunerHost(extra.Id);
        }

        foreach (var extra in ourGuides.Skip(1))
        {
            _listings.DeleteListingsProvider(extra.Id);
        }

        var tuner = ourTuners.FirstOrDefault();
        if (tuner is null || tuner.Url != m3u)
        {
            tuner ??= new TunerHostInfo();
            tuner.Type = "m3u";
            tuner.Url = m3u;
            tuner.FriendlyName = "DRTV";
            tuner.TunerCount = 0;
            tuner.AllowStreamSharing = true;
            tuner.AllowHWTranscoding = true;
            await _tuners.SaveTunerHost(tuner).ConfigureAwait(false);
            changed = true;
        }

        var listing = ourGuides.FirstOrDefault();
        if (listing is null || listing.Path != guide)
        {
            listing ??= new ListingsProviderInfo();
            listing.Type = "xmltv";
            listing.Path = guide;
            listing.EnableAllTuners = true;
            listing.KidsCategories = ["Kids"];
            listing.MovieCategories = ["Movie"];
            listing.SportsCategories = ["Sports"];
            listing.NewsCategories = ["News"];
            await _listings.SaveListingProvider(listing, false, false).ConfigureAwait(false);
            changed = true;
        }

        if (changed)
        {
            _logger.LogInformation("JellyDRTV: DRTV live channels and guide added to Live TV ({Url})", m3u);
            RefreshGuide();
        }
    }

    /// <summary>Starts Jellyfin's own "Refresh Guide" task so channels and programmes show up.</summary>
    public void RefreshGuide()
    {
        var task = _tasks.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask.Key == "RefreshGuide");
        if (task is not null && task.State != TaskState.Running)
        {
            _ = _tasks.Execute(task, new TaskOptions());
        }
    }
}
