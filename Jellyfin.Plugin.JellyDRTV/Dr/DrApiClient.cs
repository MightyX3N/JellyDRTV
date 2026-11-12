using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Dr;

/// <summary>
/// Talks to DRTV's public web API (the same endpoints dr.dk/drtv and yt-dlp use).
/// </summary>
public partial class DrApiClient
{
    private const string CatalogApi = "https://production-cdn.dr-massive.com/api";
    private const string AccountApi = "https://production.dr-massive.com/api";
    private const string TokenUrl = "https://isl.dr-massive.com/api/authorization/anonymous-sso?device=phone_android&lang=da&supportFallbackToken=true";
    private const string CommonQuery = "device=web_browser&lang=da&sub=Anonymous&segments=drtv%2Coptedout&geoLocation=dk&isDeviceAbroad=false";

    /// <summary>The A-Å list that contains every title on DRTV, split by first letter.</summary>
    private const string AzListId = "348";

    private static readonly string[] AzKeys =
    [
        "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v",
        "w", "x", "y", "z", "Æ", "Ø", "Å", "0-9"
    ];

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static string? _token;
    private static DateTime _tokenExpires = DateTime.MinValue;

    private readonly ILogger _logger;

    public DrApiClient(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Gets the shared HTTP client (also used for images and playlists).</summary>
    public static HttpClient HttpClient => Http;

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) JellyDRTV/1.0");
        return client;
    }

    /// <summary>Fetches every title (series, films, programs) listed in DRTV's A-Å.</summary>
    public async Task<List<DrTitle>> GetCatalogAsync(CancellationToken ct)
    {
        var result = new System.Collections.Concurrent.ConcurrentDictionary<string, DrTitle>();
        await Parallel.ForEachAsync(AzKeys, new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (key, token) =>
        {
            var page = 1;
            while (true)
            {
                var url = $"{CatalogApi}/lists/{AzListId}?page={page}&page_size=100&param={Uri.EscapeDataString("TitleGroupKey:" + key)}&{CommonQuery}";
                var json = await GetJsonAsync(url, null, token).ConfigureAwait(false);
                var items = json?["items"] as JsonArray;
                if (items is null || items.Count == 0)
                {
                    break;
                }

                foreach (var item in items)
                {
                    var t = ParseTitle(item);
                    if (t is not null)
                    {
                        result.TryAdd(t.Id, t);
                    }
                }

                var total = json?["paging"]?["total"]?.GetValue<int>() ?? 1;
                if (page >= total)
                {
                    break;
                }

                page++;
            }
        }).ConfigureAwait(false);

        return result.Values.ToList();
    }

    /// <summary>Searches the catalog by title (simple contains match on the A-Å list).</summary>
    public static IEnumerable<DrTitle> Search(IEnumerable<DrTitle> catalog, string query)
    {
        var q = Normalize(query);
        return catalog
            .Select(t => (t, n: Normalize(t.Title)))
            .Where(x => x.n.Contains(q, StringComparison.Ordinal))
            .OrderBy(x => x.n.StartsWith(q, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(x => x.t.Title, StringComparer.Create(CultureInfo.GetCultureInfo("da-DK"), true))
            .Select(x => x.t);
    }

    private static string Normalize(string s) => s.ToLower(CultureInfo.GetCultureInfo("da-DK")).Trim();

    /// <summary>Gets a single item (episode, program, season, show) with all details.</summary>
    public Task<JsonNode?> GetItemAsync(string id, CancellationToken ct)
        => GetJsonAsync($"{CatalogApi}/items/{Uri.EscapeDataString(id)}?expand=all&{CommonQuery}", null, ct);

    /// <summary>Gets a title (film/program/show) by id.</summary>
    public async Task<DrTitle?> GetTitleAsync(string id, CancellationToken ct)
    {
        var json = await GetItemAsync(id, ct).ConfigureAwait(false);
        return json is null ? null : ParseTitle(json);
    }

    /// <summary>Gets a series with all seasons and episodes.</summary>
    public async Task<DrSeries?> GetSeriesAsync(string showId, CancellationToken ct)
    {
        var show = await GetItemAsync(showId, ct).ConfigureAwait(false);
        return show is null ? null : await GetSeriesFromShowAsync(showId, show, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A short fingerprint of a series: its seasons and how many episodes each has. If it's unchanged since
    /// the last sync, the seasons don't need to be fetched again.
    /// </summary>
    public static string? SeriesFingerprint(JsonNode? show)
    {
        if (show is null || (string?)show["type"] != "show" || show["seasons"]?["items"] is not JsonArray seasons)
        {
            return null;
        }

        var parts = seasons.Select(x => $"{(string?)x?["id"]}:{GetInt(x?["episodeCount"])}:{GetInt(x?["availableEpisodeCount"])}");
        var paging = (string?)show["seasons"]?["paging"]?["next"];
        return string.Join('|', parts) + (paging is null ? string.Empty : "|more") + "|" + (string?)show["title"];
    }

    /// <summary>Gets a series from an already fetched show item.</summary>
    public async Task<DrSeries?> GetSeriesFromShowAsync(string showId, JsonNode show, CancellationToken ct)
    {
        if ((string?)show["type"] != "show")
        {
            return null;
        }

        var series = new DrSeries { Title = ParseTitle(show) ?? new DrTitle { Id = showId } };
        var seasonNodes = await CollectPagedAsync(show["seasons"], ct).ConfigureAwait(false);
        var seasonIds = seasonNodes.Select(s => (string?)s?["id"]).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).ToList();
        var loaded = new DrSeason?[seasonIds.Count];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, seasonIds.Count),
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (i, token) => loaded[i] = await GetSeasonAsync(seasonIds[i], token).ConfigureAwait(false)).ConfigureAwait(false);

        var usedNumbers = new HashSet<int>();
        for (var i = 0; i < loaded.Length; i++)
        {
            var season = loaded[i];
            if (season is null)
            {
                continue;
            }

            if (season.SeasonNumber <= 0)
            {
                season.SeasonNumber = i + 1;
            }

            // Two seasons with the same number would collide on disk.
            while (!usedNumbers.Add(season.SeasonNumber))
            {
                season.SeasonNumber++;
            }

            series.Seasons.Add(season);
        }

        return series;
    }

    /// <summary>Gets one season with its episodes.</summary>
    public async Task<DrSeason?> GetSeasonAsync(string seasonId, CancellationToken ct)
    {
        var json = await GetItemAsync(seasonId, ct).ConfigureAwait(false);
        if (json is null || (string?)json["type"] != "season")
        {
            return null;
        }

        var season = new DrSeason
        {
            Id = seasonId,
            Title = (string?)json["title"] ?? string.Empty,
            SeasonNumber = GetInt(json["seasonNumber"]) ?? 0,
            PosterUrl = Resize((string?)json["images"]?["poster"], 400, 600),
        };

        var episodes = await CollectPagedAsync(json["episodes"], ct).ConfigureAwait(false);
        var index = 0;
        var used = new HashSet<int>();
        foreach (var e in episodes)
        {
            index++;
            var ep = ParseEpisode(e);
            if (ep is null)
            {
                continue;
            }

            if (ep.EpisodeNumber <= 0)
            {
                ep.EpisodeNumber = index;
            }

            while (!used.Add(ep.EpisodeNumber))
            {
                ep.EpisodeNumber++;
            }

            season.Episodes.Add(ep);
        }

        return season;
    }

    /// <summary>Resolves the playable stream for an episode or program.</summary>
    public async Task<DrStream?> GetStreamAsync(string itemId, CancellationToken ct)
    {
        var token = await GetTokenAsync(ct).ConfigureAwait(false);
        var url = $"{AccountApi}/account/items/{Uri.EscapeDataString(itemId)}/videos?delivery=stream&device=web_browser&ff=idp%2Cldp%2Crpt&lang=da&resolution=HD-1080&sub=Anonymous";
        var json = await GetJsonAsync(url, token, ct).ConfigureAwait(false);
        if (json is not JsonArray streams || streams.Count == 0)
        {
            return null;
        }

        // Prefer the normal version over sign language / audio description variants.
        var chosen = streams.FirstOrDefault(s => (string?)s?["accessService"] == "StandardVideo" && !string.IsNullOrEmpty((string?)s?["url"]))
                     ?? streams.FirstOrDefault(s => !string.IsNullOrEmpty((string?)s?["url"]));
        if (chosen is null)
        {
            return null;
        }

        var stream = new DrStream
        {
            Url = (string)chosen["url"]!,
            Drm = (string?)chosen["drm"] ?? "None",
        };
        if (chosen["subtitles"] is JsonArray subs)
        {
            foreach (var s in subs)
            {
                var link = (string?)s?["link"];
                if (!string.IsNullOrEmpty(link))
                {
                    stream.Subtitles.Add(new DrSubtitle { Url = link, Language = (string?)s?["language"] ?? string.Empty });
                }
            }
        }

        return stream;
    }

    /// <summary>
    /// Downloads a HLS master playlist, keeps only the best video variant (with its audio and the subtitles)
    /// and makes every URI absolute, so Jellyfin sees one clean stream instead of five quality levels.
    /// </summary>
    public static async Task<string> GetAbsolutePlaylistAsync(string masterUrl, CancellationToken ct)
    {
        var text = await Http.GetStringAsync(masterUrl, ct).ConfigureAwait(false);
        var baseUri = new Uri(masterUrl);
        string Abs(string u) => new Uri(baseUri, u.Trim()).AbsoluteUri;
        string FixTag(string l) => UriAttribute().Replace(l, m => "URI=\"" + Abs(m.Groups[1].Value) + "\"");

        var lines = text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');

        // Find the variant with the highest bandwidth.
        string? bestInf = null, bestUri = null;
        long bestBw = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            var uriLine = lines.Skip(i + 1).FirstOrDefault(l => l.Length > 0 && !l.StartsWith('#'));
            var bw = Attr(lines[i], "BANDWIDTH") is { } b && long.TryParse(b, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if (uriLine is not null && bw > bestBw)
            {
                bestBw = bw;
                bestInf = lines[i];
                bestUri = uriLine;
            }
        }

        var sb = new StringBuilder();
        if (bestInf is null)
        {
            // Not a master playlist: just make it absolute.
            foreach (var l in lines)
            {
                sb.Append(l.Length == 0 ? l : l.StartsWith('#') ? FixTag(l) : Abs(l)).Append('\n');
            }

            return sb.ToString();
        }

        var audioGroup = Attr(bestInf, "AUDIO");
        foreach (var l in lines)
        {
            if (l.Length == 0 || !l.StartsWith('#') || l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal) || l.StartsWith("#EXT-X-I-FRAME-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            if (l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var type = Attr(l, "TYPE");
                if (type == "AUDIO" && Attr(l, "GROUP-ID") != audioGroup)
                {
                    continue;
                }
            }

            sb.Append(FixTag(l)).Append('\n');
        }

        sb.Append(bestInf).Append('\n').Append(Abs(bestUri!)).Append('\n');
        return sb.ToString();
    }

    private static string? Attr(string line, string name)
    {
        var m = Regex.Match(line, "(?:^|[:,])" + Regex.Escape(name) + "=(\"[^\"]*\"|[^,]*)");
        return m.Success ? m.Groups[1].Value.Trim('"') : null;
    }

    [GeneratedRegex("URI=\"([^\"]+)\"")]
    private static partial Regex UriAttribute();

    private async Task<List<JsonNode?>> CollectPagedAsync(JsonNode? list, CancellationToken ct)
    {
        var all = new List<JsonNode?>();
        var current = list;
        var guard = 0;
        while (current is not null && guard++ < 100)
        {
            if (current["items"] is JsonArray items)
            {
                all.AddRange(items.Select(i => i?.DeepClone()));
            }

            var next = (string?)current["paging"]?["next"];
            if (string.IsNullOrEmpty(next))
            {
                break;
            }

            var url = CatalogApi + next + (next.Contains("device=", StringComparison.Ordinal) ? string.Empty : "&" + CommonQuery);
            current = await GetJsonAsync(url, null, ct).ConfigureAwait(false);
        }

        return all;
    }

    private static DrTitle? ParseTitle(JsonNode? item)
    {
        if (item is null)
        {
            return null;
        }

        var type = (string?)item["type"];
        var id = (string?)item["id"];
        if (string.IsNullOrEmpty(id) || (type != "show" && type != "program"))
        {
            return null;
        }

        var categories = ReadStrings(item["categories"]);
        var displayCategory = (string?)item["customFields"]?["DisplayCategory"] ?? string.Empty;
        var isFilm = categories.Any(c => c is "film" or "spillefilm" or "julefilm" or "familiefilm")
                     || displayCategory.Split(',').Any(c => c.Trim().Equals("Film", StringComparison.OrdinalIgnoreCase));

        var genres = displayCategory.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return new DrTitle
        {
            Id = id,
            Kind = type == "show" ? DrKind.Series : (isFilm ? DrKind.Movie : DrKind.Program),
            Title = ((string?)item["title"] ?? id).Trim(),
            Path = (string?)item["path"] ?? string.Empty,
            Year = GetInt(item["releaseYear"]),
            Description = (string?)item["description"] is { Length: > 0 } d ? d : ((string?)item["shortDescription"] ?? string.Empty),
            PosterUrl = Resize((string?)item["images"]?["poster"], 400, 600),
            FanartUrl = Resize((string?)item["images"]?["wallpaper"], 1280, 720),
            Genres = genres,
            Categories = categories,
            DurationSeconds = GetInt(item["duration"]),
            Aired = ParseDate((string?)item["customFields"]?["BroadcastTimeDK"]) ?? ParseDate((string?)item["customFields"]?["AvailableFrom"]),
        };
    }

    private static DrEpisode? ParseEpisode(JsonNode? item)
    {
        var id = (string?)item?["id"];
        if (item is null || string.IsNullOrEmpty(id))
        {
            return null;
        }

        return new DrEpisode
        {
            Id = id,
            Title = ((string?)item["title"] ?? string.Empty).Trim(),
            EpisodeName = ((string?)item["episodeName"] ?? (string?)item["contextualTitle"] ?? (string?)item["title"] ?? string.Empty).Trim(),
            EpisodeNumber = GetInt(item["episodeNumber"]) ?? 0,
            Path = (string?)item["path"] ?? string.Empty,
            Description = (string?)item["description"] is { Length: > 0 } d ? d : ((string?)item["shortDescription"] ?? string.Empty),
            DurationSeconds = GetInt(item["duration"]),
            ThumbUrl = Resize((string?)item["images"]?["wallpaper"] ?? (string?)item["images"]?["tile"], 480, 270),
            Aired = ParseDate((string?)item["customFields"]?["BroadcastTimeDK"]) ?? ParseDate((string?)item["customFields"]?["AvailableFrom"]),
        };
    }

    private static List<string> ReadStrings(JsonNode? node)
        => node is JsonArray arr ? arr.Select(x => (string?)x).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!.ToLowerInvariant()).ToList() : [];

    private static int? GetInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValueKind() switch
            {
                JsonValueKind.Number => (int)node.GetValue<double>(),
                JsonValueKind.String => int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null,
                _ => null
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>DR's image URLs take Width/Height parameters; ask for a sensible size.</summary>
    private static string? Resize(string? url, int width, int height)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        url = WidthParam().Replace(url, "Width=" + width.ToString(CultureInfo.InvariantCulture));
        url = HeightParam().Replace(url, "Height=" + height.ToString(CultureInfo.InvariantCulture));
        url = url.Replace("Format='png'", "Format='jpg'", StringComparison.Ordinal);
        return QualityParam().Replace(url, "Quality=75");
    }

    [GeneratedRegex("Width=\\d+")]
    private static partial Regex WidthParam();

    [GeneratedRegex("Height=\\d+")]
    private static partial Regex HeightParam();

    [GeneratedRegex("Quality=\\d+")]
    private static partial Regex QualityParam();

    private async Task<string> GetTokenAsync(CancellationToken ct)
    {
        await TokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is not null && DateTime.UtcNow < _tokenExpires)
            {
                return _token;
            }

            var body = JsonSerializer.Serialize(new { deviceId = Guid.NewGuid().ToString(), scopes = new[] { "Catalog" }, optout = true });
            using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false)) as JsonArray;
            var tokenNode = json?.FirstOrDefault(t => (string?)t?["type"] == "UserAccount");
            _token = (string?)tokenNode?["value"] ?? throw new InvalidOperationException("DR did not return an anonymous token");
            var expires = ParseDate((string?)tokenNode?["expirationDate"]) ?? DateTime.UtcNow.AddHours(1);
            _tokenExpires = expires.AddMinutes(-5);
            return _token;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    private async Task<JsonNode?> GetJsonAsync(string url, string? bearer, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (bearer is not null)
                {
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
                }

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                if ((int)resp.StatusCode >= 500 || resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    throw new HttpRequestException($"DR returned {(int)resp.StatusCode}");
                }

                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("DR returned {Status} for {Url}", (int)resp.StatusCode, url);
                    return null;
                }

                var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return JsonNode.Parse(text);
            }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
