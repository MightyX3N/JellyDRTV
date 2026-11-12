using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.JellyDRTV.Dr;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Live;

/// <summary>A DR channel that can be watched live.</summary>
public class DrChannel
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public int Number { get; set; }

    public string HlsUrl { get; set; } = string.Empty;

    public string? LogoUrl { get; set; }

    /// <summary>Gets the id used to match the channel in the M3U and the XMLTV guide.</summary>
    public string TvgId => "drtv-" + Id;
}

/// <summary>
/// DR's live channels and TV guide, served as an M3U playlist and an XMLTV file for Jellyfin's Live TV.
/// </summary>
public class DrLive
{
    private const string CatalogApi = "https://production-cdn.dr-massive.com/api";
    private const string CommonQuery = "device=web_browser&lang=da&sub=Anonymous&segments=drtv%2Coptedout&geoLocation=dk&isDeviceAbroad=false";

    /// <summary>DR's TV guide list; it lists DR's channels (with streams) and other Danish channels (guide only).</summary>
    private const string ChannelListId = "195869";

    private static readonly SemaphoreSlim GuideLock = new(1, 1);
    private readonly ILogger _logger;
    private List<DrChannel>? _channels;
    private DateTime _channelsFetched = DateTime.MinValue;
    private string? _guide;
    private DateTime _guideFetched = DateTime.MinValue;

    public DrLive(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Gets DR's channels that have a live stream (DR1, DR2, DR Ramasjang, TVA ...).</summary>
    public async Task<List<DrChannel>> GetChannelsAsync(CancellationToken ct)
    {
        if (_channels is not null && DateTime.UtcNow - _channelsFetched < TimeSpan.FromHours(6))
        {
            return _channels;
        }

        var json = await GetJsonAsync($"{CatalogApi}/lists/{ChannelListId}?page=1&page_size=100&{CommonQuery}", ct).ConfigureAwait(false);
        var list = new List<DrChannel>();
        if (json?["items"] is JsonArray items)
        {
            foreach (var c in items)
            {
                var hls = (string?)c?["customFields"]?["hlsURL"];
                var id = (string?)c?["id"];
                if (string.IsNullOrEmpty(id) || hls is null || !hls.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                list.Add(new DrChannel
                {
                    Id = id,
                    Name = ((string?)c?["title"] ?? id).Trim(),
                    Number = list.Count + 1,
                    HlsUrl = hls,
                    LogoUrl = (string?)c?["images"]?["logo"] ?? (string?)c?["images"]?["square"],
                });
            }
        }

        if (list.Count > 0)
        {
            _channels = list;
            _channelsFetched = DateTime.UtcNow;
        }

        return _channels ?? list;
    }

    /// <summary>Builds the M3U playlist Jellyfin's tuner reads. Streams go through the plugin's own endpoint.</summary>
    public async Task<string> BuildM3uAsync(string serverUrl, CancellationToken ct)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var c in await GetChannelsAsync(ct).ConfigureAwait(false))
        {
            sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:-1 tvg-id=\"{c.TvgId}\" tvg-chno=\"{c.Number}\" tvg-name=\"{Attr(c.Name)}\"");
            if (!string.IsNullOrEmpty(c.LogoUrl))
            {
                sb.Append(CultureInfo.InvariantCulture, $" tvg-logo=\"{Attr(c.LogoUrl)}\"");
            }

            sb.Append(CultureInfo.InvariantCulture, $" group-title=\"DR\",{c.Name}\n");
            sb.Append(serverUrl).Append("/JellyDRTV/Live/").Append(c.Id).Append("/master.m3u8\n");
        }

        return sb.ToString();
    }

    /// <summary>Builds an XMLTV guide from DR's schedule: yesterday until a week ahead. Cached for an hour.</summary>
    public async Task<string> BuildGuideAsync(CancellationToken ct)
    {
        await GuideLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_guide is not null && DateTime.UtcNow - _guideFetched < TimeSpan.FromHours(1))
            {
                return _guide;
            }

            var channels = await GetChannelsAsync(ct).ConfigureAwait(false);
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<tv generator-info-name=\"JellyDRTV\">\n");
            foreach (var c in channels)
            {
                sb.Append(CultureInfo.InvariantCulture, $"  <channel id=\"{c.TvgId}\">\n    <display-name>{X(c.Name)}</display-name>\n");
                if (!string.IsNullOrEmpty(c.LogoUrl))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"    <icon src=\"{X(c.LogoUrl)}\" />\n");
                }

                sb.Append("  </channel>\n");
            }

            var ids = string.Join("%2C", channels.Select(c => c.Id));
            var kidsChannels = channels.Where(c => c.Name.Contains("Ramasjang", StringComparison.OrdinalIgnoreCase)).Select(c => c.Id).ToHashSet();
            var seen = new HashSet<string>();
            var today = DateTime.UtcNow.Date;
            for (var day = -1; day <= 7 && channels.Count > 0; day++)
            {
                var date = today.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                var json = await GetJsonAsync($"{CatalogApi}/schedules?channels={ids}&date={date}&hour=0&duration=24&{CommonQuery}", ct).ConfigureAwait(false);
                if (json is not JsonArray perChannel)
                {
                    continue;
                }

                foreach (var ch in perChannel)
                {
                    var channelId = (string?)ch?["channelId"];
                    if (channelId is null || ch?["schedules"] is not JsonArray schedules)
                    {
                        continue;
                    }

                    foreach (var s in schedules)
                    {
                        AppendProgramme(sb, channelId, s, kidsChannels.Contains(channelId), seen);
                    }
                }
            }

            sb.Append("</tv>\n");
            _guide = sb.ToString();
            _guideFetched = DateTime.UtcNow;
            return _guide;
        }
        finally
        {
            GuideLock.Release();
        }
    }

    private static void AppendProgramme(StringBuilder sb, string channelId, JsonNode? s, bool kidsChannel, HashSet<string> seen)
    {
        var start = ParseDate((string?)s?["startDate"]);
        var end = ParseDate((string?)s?["endDate"]);
        var item = s?["item"];
        if (start is null || end is null || item is null || end <= start || !seen.Add(channelId + start.Value.Ticks))
        {
            return;
        }

        var title = ((string?)item["title"] ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            return;
        }

        sb.Append(CultureInfo.InvariantCulture, $"  <programme start=\"{T(start.Value)}\" stop=\"{T(end.Value)}\" channel=\"drtv-{channelId}\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"    <title lang=\"da\">{X(title)}</title>\n");
        var sub = (string?)item["episodeName"] ?? (string?)item["contextualTitle"];
        if (!string.IsNullOrWhiteSpace(sub) && !string.Equals(sub.Trim(), title, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <sub-title lang=\"da\">{X(sub.Trim())}</sub-title>\n");
        }

        var desc = (string?)item["description"] ?? (string?)item["shortDescription"];
        if (!string.IsNullOrWhiteSpace(desc))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <desc lang=\"da\">{X(desc.Trim())}</desc>\n");
        }

        foreach (var cat in Categories(item, kidsChannel))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <category lang=\"en\">{cat}</category>\n");
        }

        var image = (string?)item["images"]?["tile"] ?? (string?)item["images"]?["wallpaper"];
        if (!string.IsNullOrEmpty(image))
        {
            sb.Append(CultureInfo.InvariantCulture, $"    <icon src=\"{X(SmallImage(image))}\" />\n");
        }

        var season = (int?)Num(item["seasonNumber"]);
        var episode = (int?)Num(item["episodeNumber"]);
        if (episode is > 0)
        {
            var s0 = season is > 0 ? (season.Value - 1).ToString(CultureInfo.InvariantCulture) : string.Empty;
            sb.Append(CultureInfo.InvariantCulture, $"    <episode-num system=\"xmltv_ns\">{s0}.{episode.Value - 1}.</episode-num>\n");
        }

        if (s?["live"]?.GetValue<bool>() == true)
        {
            sb.Append("    <live />\n");
        }

        sb.Append("  </programme>\n");
    }

    private static IEnumerable<string> Categories(JsonNode item, bool kidsChannel)
    {
        var cats = item["categories"] is JsonArray arr ? arr.Select(c => ((string?)c ?? string.Empty).ToLowerInvariant()).ToList() : [];
        if (kidsChannel || cats.Any(c => c is "boern" or "børn" or "ramasjang" or "minisjang" or "ultra"))
        {
            yield return "Kids";
        }

        if (cats.Any(c => c is "film" or "spillefilm"))
        {
            yield return "Movie";
        }

        if (cats.Any(c => c.StartsWith("sport", StringComparison.Ordinal)))
        {
            yield return "Sports";
        }

        if (cats.Any(c => c is "nyheder" or "nyhederogdebat" or "dokumentar"))
        {
            yield return "News";
        }
    }

    /// <summary>Gets the stream address for a channel.</summary>
    public async Task<string?> GetStreamUrlAsync(string channelId, CancellationToken ct)
        => (await GetChannelsAsync(ct).ConfigureAwait(false)).FirstOrDefault(c => c.Id == channelId)?.HlsUrl;

    private static string SmallImage(string url)
        => url.Replace("Format='png'", "Format='jpg'", StringComparison.Ordinal)
              .Replace("Width=3000", "Width=640", StringComparison.Ordinal)
              .Replace("Height=1688", "Height=360", StringComparison.Ordinal);

    private static double? Num(JsonNode? n)
    {
        try
        {
            return n is null ? null : n.GetValue<double>();
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static DateTime? ParseDate(string? s)
        => DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    private static string T(DateTime utc) => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private static string X(string s) => SecurityElement.Escape(s) ?? string.Empty;

    private static string Attr(string s) => s.Replace("\"", "'", StringComparison.Ordinal);

    private async Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            var text = await DrApiClient.HttpClient.GetStringAsync(url, ct).ConfigureAwait(false);
            return JsonNode.Parse(text);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            _logger.LogWarning("JellyDRTV: live TV request failed ({Url}): {Message}", url, ex.Message);
            return null;
        }
    }
}
