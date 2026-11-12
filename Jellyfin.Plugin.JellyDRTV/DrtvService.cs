using Jellyfin.Plugin.JellyDRTV.Configuration;
using Jellyfin.Plugin.JellyDRTV.Dr;
using Jellyfin.Plugin.JellyDRTV.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV;

/// <summary>
/// The core of the plugin: syncing the catalog to disk, the download queue and followed series.
/// </summary>
public class DrtvService
{
    private readonly ILogger<DrtvService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly DrApiClient _api;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly SemaphoreSlim _queueSignal = new(0);

    public DrtvService(ILogger<DrtvService> logger, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IApplicationPaths paths)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;
        _api = new DrApiClient(logger);
        State = new StateStore(Path.Combine(paths.PluginConfigurationsPath, "JellyDRTV"));
    }

    public StateStore State { get; }

    public DrApiClient Api => _api;

    public static PluginConfiguration Config => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool IsSyncing => _syncLock.CurrentCount == 0;

    /// <summary>
    /// Tells Jellyfin a file or folder changed, so it refreshes just that part instead of scanning the whole library.
    /// </summary>
    public void NotifyChanged(string path)
    {
        try
        {
            _libraryMonitor.ReportFileSystemChanged(path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("JellyDRTV: could not report change for {Path}: {Message}", path, ex.Message);
        }
    }

    /// <summary>Gets a semaphore that is released whenever a job is queued.</summary>
    public SemaphoreSlim QueueSignal => _queueSignal;

    public LibraryWriter? CreateWriter()
    {
        var cfg = Config;
        if (string.IsNullOrWhiteSpace(cfg.LibraryPath))
        {
            return null;
        }

        Directory.CreateDirectory(cfg.LibraryPath);
        foreach (var f in new[] { LibraryWriter.SeriesFolder, LibraryWriter.MoviesFolder, LibraryWriter.ProgramsFolder })
        {
            Directory.CreateDirectory(Path.Combine(cfg.LibraryPath, f));
        }

        return new LibraryWriter(Path.GetFullPath(cfg.LibraryPath), cfg.ServerUrl, cfg.DownloadEpisodeThumbnails, _logger);
    }

    /// <summary>Fetches the whole DRTV catalog and writes it to the library folder.</summary>
    public async Task FullSyncAsync(IProgress<double> progress, CancellationToken ct)
    {
        var writer = CreateWriter() ?? throw new InvalidOperationException("Set the library folder in the JellyDRTV settings first.");
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress.Report(0);
            var catalog = await _api.GetCatalogAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("JellyDRTV: DRTV catalog has {Count} titles", catalog.Count);
            if (catalog.Count < 100)
            {
                throw new InvalidOperationException($"DR only returned {catalog.Count} titles; refusing to update the library (DR may be down or blocking this server).");
            }

            progress.Report(3);
            var entries = new System.Collections.Concurrent.ConcurrentDictionary<string, LibraryEntry>();
            var folders = new System.Collections.Concurrent.ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            var failedShows = new System.Collections.Concurrent.ConcurrentBag<string>();
            var series = new System.Collections.Concurrent.ConcurrentBag<DrSeries>();
            var done = 0;

            await Parallel.ForEachAsync(
                catalog,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Config.SyncParallelism, 1, 16), CancellationToken = ct },
                async (title, token) =>
                {
                    try
                    {
                        if (title.Kind == DrKind.Series)
                        {
                            var s = await _api.GetSeriesAsync(title.Id, token).ConfigureAwait(false);
                            if (s is null)
                            {
                                failedShows.Add(title.Id);
                            }
                            else if (s.Seasons.Sum(x => x.Episodes.Count) > 0)
                            {
                                var (e, f) = await writer.WriteSeriesAsync(s, token).ConfigureAwait(false);
                                foreach (var x in e)
                                {
                                    entries[x.DrId] = x;
                                }

                                foreach (var kv in f)
                                {
                                    folders[kv.Key] = kv.Value;
                                }

                                series.Add(s);
                            }
                        }
                        else
                        {
                            var entry = await writer.WriteTitleAsync(title, token).ConfigureAwait(false);
                            entries[entry.DrId] = entry;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (title.Kind == DrKind.Series)
                        {
                            failedShows.Add(title.Id);
                        }

                        _logger.LogWarning("JellyDRTV: could not sync {Title} ({Id}): {Message}", title.Title, title.Id, ex.Message);
                    }

                    var n = Interlocked.Increment(ref done);
                    progress.Report(3 + (90.0 * n / catalog.Count));
                }).ConfigureAwait(false);

            // Remove placeholders for things DR no longer has (keep shows that only failed to load this time).
            var failed = failedShows.ToHashSet();
            var catalogIds = catalog.Select(c => c.Id).ToHashSet();
            var removed = 0;
            State.Update(state =>
            {
                foreach (var old in state.Items.Values.ToList())
                {
                    if (entries.ContainsKey(old.DrId))
                    {
                        continue;
                    }

                    var keep = (old.ShowId is not null && failed.Contains(old.ShowId))
                               || (old.Kind != "episode" && catalogIds.Contains(old.DrId));
                    if (keep)
                    {
                        entries[old.DrId] = old;
                        continue;
                    }

                    writer.RemoveEntry(old);
                    removed++;
                }

                foreach (var kv in state.Folders.Where(kv => !folders.ContainsKey(kv.Key)))
                {
                    if (failed.Contains(kv.Value))
                    {
                        folders[kv.Key] = kv.Value;
                    }
                    else
                    {
                        writer.RemoveEmptyDirs(kv.Key);
                    }
                }

                state.Items = new Dictionary<string, LibraryEntry>(entries);
                state.Folders = new Dictionary<string, string>(folders, StringComparer.Ordinal);
                state.Catalog = catalog;
                state.LastSync = DateTime.UtcNow;
                state.LastSyncResult = $"{catalog.Count} titles, {entries.Count(e => e.Value.Kind == "episode")} episodes, {removed} removed, {failed.Count} series failed";
            });

            var queued = EnqueueFollowed(series);
            _logger.LogInformation("JellyDRTV: sync done ({Result}); {Queued} new episodes queued from followed series", State.Read(s => s.LastSyncResult), queued);
            progress.Report(97);
            _libraryManager.QueueLibraryScan();
            progress.Report(100);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Re-checks only the followed series and queues their new episodes.</summary>
    public async Task SyncFollowedAsync(IProgress<double> progress, CancellationToken ct)
    {
        var writer = CreateWriter();
        var followed = Config.FollowedShows.ToList();
        if (writer is null || followed.Count == 0)
        {
            progress.Report(100);
            return;
        }

        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var list = new List<DrSeries>();
            var i = 0;
            foreach (var f in followed)
            {
                var s = await WriteSeriesNoLockAsync(writer, f.ShowId, ct).ConfigureAwait(false);
                if (s is not null)
                {
                    list.Add(s);
                }

                progress.Report(100.0 * ++i / followed.Count);
            }

            foreach (var s in list)
            {
                NotifyChanged(writer.SeriesDir(s.Title));
            }

            EnqueueFollowed(list);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    /// <summary>Fetches one series, writes it and stores its entries.</summary>
    private async Task<DrSeries?> WriteSeriesNoLockAsync(LibraryWriter writer, string showId, CancellationToken ct)
    {
        var s = await _api.GetSeriesAsync(showId, ct).ConfigureAwait(false);
        if (s is null)
        {
            return null;
        }

        var (entries, folders) = await writer.WriteSeriesAsync(s, ct).ConfigureAwait(false);
        var ids = entries.Select(e => e.DrId).ToHashSet();
        State.Update(state =>
        {
            // Episodes of this show that DR removed.
            foreach (var old in state.Items.Values.Where(e => e.ShowId == showId && !ids.Contains(e.DrId)).ToList())
            {
                writer.RemoveEntry(old);
                state.Items.Remove(old.DrId);
            }

            foreach (var e in entries)
            {
                state.Items[e.DrId] = e;
            }

            foreach (var kv in folders)
            {
                state.Folders[kv.Key] = kv.Value;
            }
        });
        return s;
    }

    private int EnqueueFollowed(IEnumerable<DrSeries> seriesList)
    {
        var followed = Config.FollowedShows.ToDictionary(f => f.ShowId);
        var queued = 0;
        foreach (var s in seriesList)
        {
            if (!followed.TryGetValue(s.Title.Id, out var f))
            {
                continue;
            }

            var ignored = f.IgnoredEpisodeIds.ToHashSet();
            foreach (var ep in s.Seasons.SelectMany(x => x.Episodes))
            {
                if (!ignored.Contains(ep.Id) && Enqueue(ep.Id, quiet: true))
                {
                    queued++;
                }
            }
        }

        return queued;
    }

    /// <summary>
    /// Makes sure a DR item (episode, season, series, film, program) exists in the library and queues it for download.
    /// Returns how many downloads were queued.
    /// </summary>
    public async Task<int> DownloadAsync(string drId, CancellationToken ct)
    {
        var writer = CreateWriter() ?? throw new InvalidOperationException("Set the library folder in the JellyDRTV settings first.");
        if (State.Read(s => s.Items.ContainsKey(drId)))
        {
            return Enqueue(drId) ? 1 : 0;
        }

        var item = await _api.GetItemAsync(drId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException("DRTV has no item with id " + drId);
        var type = (string?)item["type"];
        switch (type)
        {
            case "program":
            {
                var t = await _api.GetTitleAsync(drId, ct).ConfigureAwait(false) ?? throw new KeyNotFoundException(drId);
                var entry = await writer.WriteTitleAsync(t, ct).ConfigureAwait(false);
                State.Update(s => s.Items[entry.DrId] = entry);
                NotifyChanged(Path.GetDirectoryName(entry.BasePath)!);
                return Enqueue(drId) ? 1 : 0;
            }

            case "episode":
            case "season":
            case "show":
            {
                var showId = type == "show" ? drId : ((string?)item["showId"] ?? (string?)item["show"]?["id"]);
                if (string.IsNullOrEmpty(showId))
                {
                    throw new KeyNotFoundException("Could not find the series for " + drId);
                }

                await _syncLock.WaitAsync(ct).ConfigureAwait(false);
                DrSeries? s;
                try
                {
                    s = await WriteSeriesNoLockAsync(writer, showId, ct).ConfigureAwait(false);
                }
                finally
                {
                    _syncLock.Release();
                }

                if (s is null)
                {
                    throw new KeyNotFoundException("Could not load the series " + showId);
                }

                NotifyChanged(writer.SeriesDir(s.Title));
                var ids = type switch
                {
                    "episode" => [drId],
                    "season" => s.Seasons.Where(x => x.Id == drId).SelectMany(x => x.Episodes).Select(e => e.Id).ToList(),
                    _ => s.Seasons.SelectMany(x => x.Episodes).Select(e => e.Id).ToList(),
                };
                return ids.Count(id => Enqueue(id, quiet: true));
            }

            default:
                throw new InvalidOperationException($"Can't download a DRTV item of type '{type}'.");
        }
    }

    /// <summary>Queues a season or a whole series that is already in the library (used by favourites).</summary>
    public int EnqueueGroup(string groupId)
    {
        var ids = State.Read(s => s.Items.Values.Where(e => e.SeasonId == groupId || e.ShowId == groupId).Select(e => e.DrId).ToList());
        return ids.Count(id => Enqueue(id, quiet: true));
    }

    /// <summary>Adds an item to the download queue unless it's already downloaded or queued.</summary>
    public bool Enqueue(string drId, bool quiet = false)
    {
        var added = false;
        State.Update(state =>
        {
            if (!state.Items.TryGetValue(drId, out var entry) || LibraryWriter.FindVideo(entry.BasePath) is not null)
            {
                return;
            }

            if (state.Queue.Any(j => j.DrId == drId && j.Status is DownloadStatus.Queued or DownloadStatus.Downloading or DownloadStatus.Skipped))
            {
                return;
            }

            // A failed earlier attempt is replaced by the new one.
            state.Queue.RemoveAll(j => j.DrId == drId && j.Status is DownloadStatus.Failed or DownloadStatus.Done);
            state.Queue.Add(new DownloadJob { DrId = drId, Title = entry.Title });
            added = true;
        });

        if (added)
        {
            _queueSignal.Release();
            if (!quiet)
            {
                _logger.LogInformation("JellyDRTV: queued {Id} for download", drId);
            }
        }

        return added;
    }

    /// <summary>Starts following a series.</summary>
    public async Task<int> FollowAsync(string showId, bool downloadExisting, CancellationToken ct)
    {
        var writer = CreateWriter() ?? throw new InvalidOperationException("Set the library folder in the JellyDRTV settings first.");
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        DrSeries? s;
        try
        {
            s = await WriteSeriesNoLockAsync(writer, showId, ct).ConfigureAwait(false);
        }
        finally
        {
            _syncLock.Release();
        }

        if (s is null)
        {
            throw new KeyNotFoundException("DRTV has no series with id " + showId);
        }

        var cfg = Plugin.Instance!.Configuration;
        cfg.FollowedShows.RemoveAll(f => f.ShowId == showId);
        cfg.FollowedShows.Add(new FollowedShow
        {
            ShowId = showId,
            Title = s.Title.Title,
            IgnoredEpisodeIds = downloadExisting ? [] : s.Seasons.SelectMany(x => x.Episodes).Select(e => e.Id).ToList(),
        });
        Plugin.Instance.SaveConfiguration();
        NotifyChanged(writer.SeriesDir(s.Title));
        return EnqueueFollowed([s]);
    }

    public void Unfollow(string showId)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (cfg.FollowedShows.RemoveAll(f => f.ShowId == showId) > 0)
        {
            Plugin.Instance.SaveConfiguration();
        }
    }

    /// <summary>
    /// Saves DR's subtitles next to the .strm file. Called when Jellyfin probes the stream, which happens right
    /// before it looks for external subtitle files, so they show up on the first play.
    /// </summary>
    public async Task WriteSidecarSubtitlesAsync(string drId, DrStream stream, CancellationToken ct)
    {
        var entry = State.Read(s => s.Items.TryGetValue(drId, out var e) ? e : null);
        if (entry is null || !File.Exists(entry.BasePath + ".strm"))
        {
            return;
        }

        await WriteSubtitlesAsync(entry, stream, ct).ConfigureAwait(false);
    }

    /// <summary>Saves DR's subtitle tracks as .vtt files next to the item (Danish, foreign-language parts, combined).</summary>
    public async Task WriteSubtitlesAsync(LibraryEntry entry, DrStream stream, CancellationToken ct)
    {
        foreach (var sub in stream.Subtitles)
        {
            var suffix = sub.Language switch
            {
                "DanishLanguageSubtitles" => "da",
                "ForeignLanguageSubtitles" => "da.foreign",
                "CombinedLanguageSubtitles" => "da.combined",
                _ => null
            };
            var path = suffix is null ? null : $"{entry.BasePath}.{suffix}.vtt";
            if (path is null || File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = await DrApiClient.HttpClient.GetStringAsync(sub.Url, ct).ConfigureAwait(false);
                if (text.StartsWith("WEBVTT", StringComparison.Ordinal))
                {
                    await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
                }
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug("JellyDRTV: could not fetch subtitles for {Id}: {Message}", entry.DrId, ex.Message);
            }
        }
    }

    /// <summary>Finds the DR id behind a file or folder in the library.</summary>
    public (string? Id, string? Kind) Lookup(string path)
    {
        var full = Path.GetFullPath(path);
        return State.Read(s =>
        {
            if (s.Folders.TryGetValue(full.TrimEnd(Path.DirectorySeparatorChar), out var folderId))
            {
                return (folderId, "group");
            }

            var basePath = Path.Combine(Path.GetDirectoryName(full) ?? string.Empty, Path.GetFileNameWithoutExtension(full));
            var entry = s.Items.Values.FirstOrDefault(e => e.BasePath == basePath);
            return (entry?.DrId, entry?.Kind);
        });
    }
}
