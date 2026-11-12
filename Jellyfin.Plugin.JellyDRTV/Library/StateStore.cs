using System.Text.Json;
using Jellyfin.Plugin.JellyDRTV.Dr;

namespace Jellyfin.Plugin.JellyDRTV.Library;

/// <summary>Where a DRTV item lives on disk.</summary>
public class LibraryEntry
{
    public string DrId { get; set; } = string.Empty;

    /// <summary>Gets or sets "episode", "movie" or "program".</summary>
    public string Kind { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the full path without extension (the .strm / video / .nfo share it).</summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the DR web path, e.g. /episode/slug_123.</summary>
    public string DrPath { get; set; } = string.Empty;

    public string? ShowId { get; set; }

    public string? SeasonId { get; set; }
}

/// <summary>Status of a download job.</summary>
public enum DownloadStatus
{
    Queued,
    Downloading,
    Done,
    Failed,
    Skipped
}

/// <summary>One item in the download queue.</summary>
public class DownloadJob
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");

    public string DrId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public double Progress { get; set; }

    public string? Message { get; set; }

    public DateTime Added { get; set; } = DateTime.UtcNow;

    public DateTime? Finished { get; set; }
}

/// <summary>Everything the plugin remembers between restarts.</summary>
public class PluginState
{
    public DateTime? LastSync { get; set; }

    public string? LastSyncResult { get; set; }

    /// <summary>Gets or sets the Jellyfin address the .strm files currently point at.</summary>
    public string? StrmBaseUrl { get; set; }

    public List<DrTitle> Catalog { get; set; } = [];

    public Dictionary<string, LibraryEntry> Items { get; set; } = [];

    /// <summary>Gets or sets folder path → DR id for series and season folders.</summary>
    public Dictionary<string, string> Folders { get; set; } = new(StringComparer.Ordinal);

    public List<DownloadJob> Queue { get; set; } = [];

    /// <summary>Gets or sets which titles (film/program/series id) are DRM-protected, so they're only checked once.</summary>
    public Dictionary<string, bool> Drm { get; set; } = [];

    /// <summary>Gets or sets each series' fingerprint from the last sync, to skip series that haven't changed.</summary>
    public Dictionary<string, string> SeriesFingerprints { get; set; } = [];
}

/// <summary>Thread-safe JSON-backed state.</summary>
public class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly string _file;
    private readonly object _lock = new();
    private PluginState _state;

    public StateStore(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _file = Path.Combine(dataFolder, "state.json");
        _state = Load();

        // A download that was running when Jellyfin stopped is retried.
        foreach (var job in _state.Queue.Where(j => j.Status == DownloadStatus.Downloading))
        {
            job.Status = DownloadStatus.Queued;
            job.Progress = 0;
        }
    }

    public T Read<T>(Func<PluginState, T> reader)
    {
        lock (_lock)
        {
            return reader(_state);
        }
    }

    public void Update(Action<PluginState> change)
    {
        lock (_lock)
        {
            change(_state);
            Save();
        }
    }

    private PluginState Load()
    {
        try
        {
            if (File.Exists(_file))
            {
                return JsonSerializer.Deserialize<PluginState>(File.ReadAllText(_file), JsonOptions) ?? new PluginState();
            }
        }
        catch (JsonException)
        {
            // Corrupt state: start over, the next sync rebuilds it.
        }

        return new PluginState();
    }

    private void Save()
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions));
        File.Move(tmp, _file, true);
    }
}
