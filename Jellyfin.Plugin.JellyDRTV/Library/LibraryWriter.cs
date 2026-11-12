using System.Globalization;
using System.Security;
using System.Text;
using Jellyfin.Plugin.JellyDRTV.Dr;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Library;

/// <summary>
/// Writes the DRTV catalog to disk in Jellyfin's folder layout: .strm placeholders, .nfo metadata and artwork.
/// </summary>
public class LibraryWriter
{
    public const string SeriesFolder = "Serier";
    public const string MoviesFolder = "Film";
    public const string ProgramsFolder = "Programmer";

    /// <summary>Extensions that count as a downloaded video replacing the .strm placeholder.</summary>
    public static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".ts"];

    private readonly string _root;
    private readonly string _serverUrl;
    private readonly bool _episodeThumbs;
    private readonly bool _badges;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _changed = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _posters = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    public LibraryWriter(string root, string serverUrl, bool episodeThumbs, bool badges, ILogger logger)
    {
        _badges = badges;
        _root = root;
        _serverUrl = serverUrl.TrimEnd('/');
        _episodeThumbs = episodeThumbs;
        _logger = logger;
    }

    public string Root => _root;

    /// <summary>Gets the title folders (series, film, program) where something changed on disk.</summary>
    public IReadOnlyCollection<string> ChangedTitleDirs => _changed.Keys.ToList();

    /// <summary>Gets the title folders whose poster was redrawn.</summary>
    public IReadOnlyCollection<string> ChangedPosterDirs => _posters.Keys.ToList();

    /// <summary>Remembers which title folder a changed file belongs to.</summary>
    private void Mark(string path)
    {
        var dir = TitleDir(path);
        if (dir is not null)
        {
            _changed.TryAdd(dir, 0);
        }
    }

    /// <summary>The folder right under Serier/Film/Programmer that contains the path.</summary>
    private string? TitleDir(string path)
    {
        var rel = Path.GetRelativePath(_root, path);
        var parts = rel.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && !rel.StartsWith("..", StringComparison.Ordinal) ? Path.Combine(_root, parts[0], parts[1]) : null;
    }

    /// <summary>Returns the downloaded video for a base path, if any.</summary>
    public static string? FindVideo(string basePath)
        => VideoExtensions.Select(e => basePath + e).FirstOrDefault(File.Exists);

    public string StreamUrl(string drId) => $"{_serverUrl}/JellyDRTV/Stream/{Uri.EscapeDataString(drId)}/master.m3u8";

    public static string SafeName(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
        }

        var s = string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.');
        if (s.Length > 120)
        {
            s = s[..120].Trim();
        }

        return s.Length == 0 ? "Uden titel" : s;
    }

    /// <summary>Folder of a series: "Title [drtv-123]".</summary>
    public string SeriesDir(DrTitle t) => Path.Combine(_root, SeriesFolder, $"{SafeName(t.Title)} [drtv-{t.Id}]");

    public static string SeasonDirName(int number) => "Season " + number.ToString("00", CultureInfo.InvariantCulture);

    public static string EpisodeBaseName(DrTitle show, DrSeason season, DrEpisode ep)
    {
        var name = $"{SafeName(show.Title)} S{season.SeasonNumber:00}E{ep.EpisodeNumber:00}";
        var epName = SafeName(ep.EpisodeName);
        if (!string.IsNullOrWhiteSpace(ep.EpisodeName) && !string.Equals(epName, SafeName(show.Title), StringComparison.OrdinalIgnoreCase))
        {
            name += " - " + epName;
        }

        return name.Length > 150 ? name[..150].Trim() : name;
    }

    /// <summary>Base path (no extension) of a film or program.</summary>
    public string TitleBasePath(DrTitle t)
    {
        var folder = t.Kind == DrKind.Movie ? MoviesFolder : ProgramsFolder;
        var name = t.Year is int y ? $"{SafeName(t.Title)} ({y})" : SafeName(t.Title);
        return Path.Combine(_root, folder, $"{name} [drtv-{t.Id}]", name);
    }

    /// <summary>Writes a film or single program. Returns its library entry.</summary>
    public async Task<LibraryEntry> WriteTitleAsync(DrTitle t, bool drm, CancellationToken ct)
    {
        var basePath = TitleBasePath(t);
        var dir = Path.GetDirectoryName(basePath)!;
        Directory.CreateDirectory(dir);

        WriteStrmIfNeeded(basePath, t.Id);
        WriteIfChanged(basePath + ".nfo", BuildMovieNfo(t, drm));
        await WritePosterAsync(t.PosterUrl, dir, drm, ct).ConfigureAwait(false);
        await FetchImageAsync(t.FanartUrl, Path.Combine(dir, "fanart.jpg"), ct).ConfigureAwait(false);

        return new LibraryEntry
        {
            DrId = t.Id,
            Kind = t.Kind == DrKind.Movie ? "movie" : "program",
            Title = t.Title,
            BasePath = basePath,
            DrPath = t.Path,
        };
    }

    /// <summary>Only updates a series' poster (used when the series itself hasn't changed).</summary>
    public Task UpdateSeriesPosterAsync(DrTitle t, bool drm, CancellationToken ct)
        => WritePosterAsync(t.PosterUrl, SeriesDir(t), drm, ct);

    /// <summary>Writes a full series. Returns entries for every episode, plus the folder → id map.</summary>
    public async Task<(List<LibraryEntry> Entries, Dictionary<string, string> Folders)> WriteSeriesAsync(DrSeries s, bool drm, CancellationToken ct)
    {
        var entries = new List<LibraryEntry>();
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var showDir = SeriesDir(s.Title);
        Directory.CreateDirectory(showDir);
        folders[showDir] = s.Title.Id;

        WriteIfChanged(Path.Combine(showDir, "tvshow.nfo"), BuildSeriesNfo(s.Title, drm));
        await WritePosterAsync(s.Title.PosterUrl, showDir, drm, ct).ConfigureAwait(false);
        await FetchImageAsync(s.Title.FanartUrl, Path.Combine(showDir, "fanart.jpg"), ct).ConfigureAwait(false);

        var thumbs = new List<(string Url, string Path)>();
        foreach (var season in s.Seasons)
        {
            var seasonDir = Path.Combine(showDir, SeasonDirName(season.SeasonNumber));
            Directory.CreateDirectory(seasonDir);
            folders[seasonDir] = season.Id;
            WriteIfChanged(Path.Combine(seasonDir, "season.nfo"), BuildSeasonNfo(season));

            foreach (var ep in season.Episodes)
            {
                var basePath = Path.Combine(seasonDir, EpisodeBaseName(s.Title, season, ep));
                WriteStrmIfNeeded(basePath, ep.Id);
                WriteIfChanged(basePath + ".nfo", BuildEpisodeNfo(s.Title, season, ep));
                if (_episodeThumbs && !string.IsNullOrEmpty(ep.ThumbUrl) && !File.Exists(basePath + "-thumb.jpg"))
                {
                    thumbs.Add((ep.ThumbUrl, basePath + "-thumb.jpg"));
                }

                entries.Add(new LibraryEntry
                {
                    DrId = ep.Id,
                    Kind = "episode",
                    Title = $"{s.Title.Title} S{season.SeasonNumber:00}E{ep.EpisodeNumber:00}",
                    BasePath = basePath,
                    DrPath = ep.Path,
                    ShowId = s.Title.Id,
                    SeasonId = season.Id,
                });
            }
        }

        // Thumbnails are the slow part of a first sync; fetch several at once.
        await Parallel.ForEachAsync(
            thumbs,
            new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct },
            async (t, token) => await FetchImageAsync(t.Url, t.Path, token).ConfigureAwait(false)).ConfigureAwait(false);

        return (entries, folders);
    }

    /// <summary>Removes the placeholder files of an item that is gone from DRTV. Downloaded videos are kept.</summary>
    public void RemoveEntry(LibraryEntry e)
    {
        if (FindVideo(e.BasePath) is not null)
        {
            return;
        }

        foreach (var f in new[] { e.BasePath + ".strm", e.BasePath + ".nfo", e.BasePath + "-thumb.jpg" })
        {
            TryDelete(f);
        }

        var dir = Path.GetDirectoryName(e.BasePath);
        if (e.Kind != "episode" && dir is not null)
        {
            // Film/program folders only hold this one title.
            TryDelete(Path.Combine(dir, "poster.jpg"));
            TryDelete(Path.Combine(dir, "fanart.jpg"));
            TryDelete(Path.Combine(dir, PosterStampFile));
        }

        RemoveEmptyDirs(dir);
    }

    /// <summary>Removes a folder chain upward while it only contains metadata.</summary>
    public void RemoveEmptyDirs(string? dir)
    {
        var stopAt = new[] { SeriesFolder, MoviesFolder, ProgramsFolder }.Select(f => Path.Combine(_root, f)).ToHashSet(StringComparer.Ordinal);
        while (dir is not null && dir.StartsWith(_root, StringComparison.Ordinal) && !stopAt.Contains(dir) && dir != _root)
        {
            if (!Directory.Exists(dir))
            {
                dir = Path.GetDirectoryName(dir);
                continue;
            }

            // Keep the folder if anything other than our own metadata/artwork is in it.
            if (Directory.GetDirectories(dir).Length > 0)
            {
                return;
            }

            foreach (var f in Directory.GetFiles(dir))
            {
                var isArt = f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(f) == PosterStampFile;
                var isOurNfo = f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase) && IsOurNfo(f);
                if (!isArt && !isOurNfo)
                {
                    return;
                }
            }

            try
            {
                Mark(dir);
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                return;
            }

            dir = Path.GetDirectoryName(dir);
        }
    }

    private static bool IsOurNfo(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains("<uniqueid type=\"drtv\"", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private void WriteStrmIfNeeded(string basePath, string drId)
    {
        // Once downloaded, the video replaces the placeholder.
        if (FindVideo(basePath) is not null)
        {
            TryDelete(basePath + ".strm");
            return;
        }

        WriteIfChanged(basePath + ".strm", StreamUrl(drId));
    }

    private void WriteIfChanged(string path, string content)
    {
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == content)
            {
                return;
            }

            File.WriteAllText(path, content, new UTF8Encoding(false));
            Mark(path);
        }
        catch (IOException)
        {
            // Another process has the file; next sync tries again.
        }
    }

    /// <summary>Name of the small file that remembers how poster.jpg was made, so it's only redrawn when needed.</summary>
    public const string PosterStampFile = ".jellydrtv-poster";

    /// <summary>Downloads the poster and draws the DRTV / DRM badges on it.</summary>
    private async Task WritePosterAsync(string? url, string dir, bool drm, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var posterPath = Path.Combine(dir, "poster.jpg");
        var stampPath = Path.Combine(dir, PosterStampFile);
        var stamp = $"{url}|badges={_badges}|drm={drm}|v={PosterBadge.Version}";
        try
        {
            if (File.Exists(posterPath) && File.Exists(stampPath) && File.ReadAllText(stampPath) == stamp)
            {
                return;
            }

            var bytes = await DrApiClient.HttpClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return;
            }

            if (_badges || drm)
            {
                bytes = PosterBadge.Apply(bytes, _badges, drm);
            }

            await File.WriteAllBytesAsync(posterPath, bytes, ct).ConfigureAwait(false);
            Mark(posterPath);
            var titleDir = TitleDir(posterPath);
            if (titleDir is not null)
            {
                _posters.TryAdd(titleDir, 0);
            }

            await File.WriteAllTextAsync(stampPath, stamp, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Could not write poster {Url}: {Message}", url, ex.Message);
        }
    }

    private async Task FetchImageAsync(string? url, string path, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(url) || File.Exists(path))
        {
            return;
        }

        try
        {
            var bytes = await DrApiClient.HttpClient.GetByteArrayAsync(url, ct).ConfigureAwait(false);
            if (bytes.Length > 0)
            {
                await File.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogDebug("Could not fetch image {Url}: {Message}", url, ex.Message);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                Mark(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private static string X(string? s) => SecurityElement.Escape(s ?? string.Empty) ?? string.Empty;

    private static string Date(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Genres(DrTitle t) => string.Concat(t.Genres.Select(g => $"  <genre>{X(g)}</genre>\n"));

    private static string BuildMovieNfo(DrTitle t, bool drm)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<movie>\n");
        sb.Append($"  <title>{X(t.Title)}</title>\n");
        sb.Append($"  <plot>{X(t.Description)}</plot>\n");
        if (t.Year is int y)
        {
            sb.Append($"  <year>{y}</year>\n");
        }

        if (t.Aired is not null)
        {
            sb.Append($"  <premiered>{Date(t.Aired)}</premiered>\n");
        }

        if (t.DurationSeconds is int d && d > 0)
        {
            sb.Append($"  <runtime>{Math.Max(1, d / 60)}</runtime>\n");
        }

        sb.Append(Genres(t));
        sb.Append("  <studio>DR</studio>\n");
        sb.Append("  <tag>DRTV</tag>\n");
        if (drm)
        {
            sb.Append("  <tag>DRM</tag>\n");
        }

        sb.Append($"  <uniqueid type=\"drtv\" default=\"true\">{X(t.Id)}</uniqueid>\n");
        sb.Append("  <lockedfields>Name|Overview|Genres|Studios</lockedfields>\n  <lockdata>true</lockdata>\n</movie>\n");
        return sb.ToString();
    }

    private static string BuildSeriesNfo(DrTitle t, bool drm)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<tvshow>\n");
        sb.Append($"  <title>{X(t.Title)}</title>\n");
        sb.Append($"  <plot>{X(t.Description)}</plot>\n");
        if (t.Year is int y)
        {
            sb.Append($"  <year>{y}</year>\n");
        }

        sb.Append(Genres(t));
        sb.Append("  <studio>DR</studio>\n");
        sb.Append("  <tag>DRTV</tag>\n");
        if (drm)
        {
            sb.Append("  <tag>DRM</tag>\n");
        }

        sb.Append($"  <uniqueid type=\"drtv\" default=\"true\">{X(t.Id)}</uniqueid>\n");
        sb.Append("  <lockedfields>Name|Overview|Genres|Studios</lockedfields>\n</tvshow>\n");
        return sb.ToString();
    }

    private static string BuildSeasonNfo(DrSeason s)
    {
        return "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<season>\n"
               + $"  <title>{X(s.Title)}</title>\n"
               + $"  <seasonnumber>{s.SeasonNumber}</seasonnumber>\n"
               + $"  <uniqueid type=\"drtv\" default=\"true\">{X(s.Id)}</uniqueid>\n"
               + "  <lockedfields>Name|Overview|Genres|Studios</lockedfields>\n</season>\n";
    }

    private static string BuildEpisodeNfo(DrTitle show, DrSeason season, DrEpisode ep)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n<episodedetails>\n");
        sb.Append($"  <title>{X(string.IsNullOrWhiteSpace(ep.EpisodeName) ? ep.Title : ep.EpisodeName)}</title>\n");
        sb.Append($"  <showtitle>{X(show.Title)}</showtitle>\n");
        sb.Append($"  <season>{season.SeasonNumber}</season>\n");
        sb.Append($"  <episode>{ep.EpisodeNumber}</episode>\n");
        sb.Append($"  <plot>{X(ep.Description)}</plot>\n");
        if (ep.Aired is not null)
        {
            sb.Append($"  <aired>{Date(ep.Aired)}</aired>\n");
        }

        if (ep.DurationSeconds is int d && d > 0)
        {
            sb.Append($"  <runtime>{Math.Max(1, d / 60)}</runtime>\n");
        }

        sb.Append($"  <uniqueid type=\"drtv\" default=\"true\">{X(ep.Id)}</uniqueid>\n");
        sb.Append("  <lockedfields>Name|Overview|Genres|Studios</lockedfields>\n  <lockdata>true</lockdata>\n</episodedetails>\n");
        return sb.ToString();
    }
}
