using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyDRTV.Tasks;

/// <summary>Daily: fetch the whole DRTV catalog and update the library folder.</summary>
public class SyncCatalogTask : IScheduledTask
{
    private readonly DrtvService _service;

    public SyncCatalogTask(DrtvService service) => _service = service;

    public string Name => "Sync DRTV catalog";

    public string Key => "JellyDRTVSyncCatalog";

    public string Description => "Fetches everything on DRTV and writes it to the JellyDRTV library folder (.strm placeholders, metadata and artwork). Removes titles DR no longer has, unless downloaded.";

    public string Category => "JellyDRTV";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.FullSyncAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.DailyTrigger, TimeOfDayTicks = TimeSpan.FromHours(4).Ticks },
    ];
}

/// <summary>Every two hours: check followed series for new episodes.</summary>
public class CheckFollowedTask : IScheduledTask
{
    private readonly DrtvService _service;

    public CheckFollowedTask(DrtvService service) => _service = service;

    public string Name => "Check followed DRTV series";

    public string Key => "JellyDRTVCheckFollowed";

    public string Description => "Looks for new episodes of the DRTV series you follow and downloads them.";

    public string Category => "JellyDRTV";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => _service.SyncFollowedAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(2).Ticks },
    ];
}
