using System.Net.Mime;
using Jellyfin.Plugin.JellyDRTV.Dr;
using Jellyfin.Plugin.JellyDRTV.Download;
using Jellyfin.Plugin.JellyDRTV.Library;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Api;

/// <summary>
/// HTTP endpoints: the stream proxy used by the .strm files, and the admin API used by the settings page.
/// </summary>
[ApiController]
[Route("JellyDRTV")]
public class JellyDrtvController : ControllerBase
{
    private static readonly MemoryCache PlaylistCache = new(new MemoryCacheOptions());
    private readonly DrtvService _service;
    private readonly ILogger<JellyDrtvController> _logger;

    public JellyDrtvController(DrtvService service, ILogger<JellyDrtvController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>
    /// The address inside every .strm file. Resolves a fresh stream from DR and returns its HLS playlist
    /// with absolute URLs. No login needed, because Jellyfin's ffmpeg calls it directly; it only ever
    /// returns DR's public playlist.
    /// </summary>
    [HttpGet("Stream/{id}/master.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> Stream([FromRoute] string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return BadRequest();
        }

        if (!PlaylistCache.TryGetValue(id, out string? playlist))
        {
            var stream = await _service.Api.GetStreamAsync(id, ct).ConfigureAwait(false);
            if (stream is null)
            {
                return NotFound("DR returned no stream for this item. It may have been removed, or this server is outside Denmark.");
            }

            if (stream.IsDrmProtected)
            {
                return StatusCode(StatusCodes.Status403Forbidden, "This DRTV title is DRM-protected and can't be played through Jellyfin.");
            }

            try
            {
                playlist = await DrApiClient.GetAbsolutePlaylistAsync(stream.Url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("JellyDRTV: DR refused the playlist for {Id}: {Message}", id, ex.Message);
                var msg = ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                    ? "DR blocked the stream. This title is limited to Denmark, so the Jellyfin server has to be in Denmark."
                    : "DR refused the stream: " + ex.Message;
                return StatusCode(StatusCodes.Status502BadGateway, msg);
            }

            PlaylistCache.Set(id, playlist, TimeSpan.FromMinutes(10));
            await _service.WriteSidecarSubtitlesAsync(id, stream, ct).ConfigureAwait(false);
        }

        return Content(playlist!, "application/vnd.apple.mpegurl");
    }

    /// <summary>Jellyfin's Live TV tuner reads this playlist of DR's live channels.</summary>
    [HttpGet("LiveTv/channels.m3u")]
    [AllowAnonymous]
    public async Task<ActionResult> LiveChannels(CancellationToken ct)
        => Content(await _service.Live.BuildM3uAsync(_service.ServerUrl, ct).ConfigureAwait(false), "audio/x-mpegurl");

    /// <summary>Jellyfin's guide reads this XMLTV file built from DR's TV schedule.</summary>
    [HttpGet("LiveTv/guide.xml")]
    [AllowAnonymous]
    public async Task<ActionResult> LiveGuide(CancellationToken ct)
        => Content(await _service.Live.BuildGuideAsync(ct).ConfigureAwait(false), "application/xml");

    /// <summary>A live channel's stream: DR's playlist with only the best quality and absolute addresses.</summary>
    [HttpGet("Live/{id}/master.m3u8")]
    [AllowAnonymous]
    public async Task<ActionResult> LiveStream([FromRoute] string id, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 32 || !id.All(char.IsAsciiLetterOrDigit))
        {
            return BadRequest();
        }

        var url = await _service.Live.GetStreamUrlAsync(id, ct).ConfigureAwait(false);
        if (url is null)
        {
            return NotFound("No live stream for this channel.");
        }

        try
        {
            return Content(await DrApiClient.GetAbsolutePlaylistAsync(url, ct).ConfigureAwait(false), "application/vnd.apple.mpegurl");
        }
        catch (HttpRequestException ex)
        {
            var msg = ex.StatusCode == System.Net.HttpStatusCode.Forbidden
                ? "DR blocked the live stream. DR's live channels only work from a server in Denmark."
                : "DR refused the live stream: " + ex.Message;
            return StatusCode(StatusCodes.Status502BadGateway, msg);
        }
    }

    /// <summary>Makes Jellyfin reload the posters of all DRTV titles (e.g. to show the badges).</summary>
    [HttpPost("RefreshPosters")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> RefreshPosters() => Ok(new { Queued = _service.RefreshAllPosters() });

    /// <summary>Plugin status for the settings page.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> Status(CancellationToken ct)
    {
        var cfg = DrtvService.Config;
        var ytdlp = DownloadWorker.Instance is null ? null : await DownloadWorker.Instance.YtDlp.GetVersionAsync(cfg.YtDlpPath, ct).ConfigureAwait(false);
        return _service.State.Read(s => (object)new
        {
            s.LastSync,
            s.LastSyncResult,
            Syncing = _service.IsSyncing,
            Titles = s.Catalog.Count,
            Episodes = s.Items.Values.Count(e => e.Kind == "episode"),
            Movies = s.Items.Values.Count(e => e.Kind == "movie"),
            Programs = s.Items.Values.Count(e => e.Kind == "program"),
            Downloaded = s.Items.Values.Count(e => LibraryWriter.FindVideo(e.BasePath) is not null),
            Drm = s.Drm.Count(d => d.Value),
            YtDlpVersion = ytdlp,
            LiveTv = cfg.LiveTv,
            LibraryPath = cfg.LibraryPath,
        });
    }

    /// <summary>Searches DRTV by title.</summary>
    [HttpGet("Search")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> Search([FromQuery] string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Ok(Array.Empty<object>());
        }

        var catalog = _service.State.Read(s => s.Catalog);
        if (catalog.Count == 0)
        {
            catalog = await _service.Api.GetCatalogAsync(ct).ConfigureAwait(false);
            _service.State.Update(s => s.Catalog = catalog);
        }

        var followed = DrtvService.Config.FollowedShows.Select(f => f.ShowId).ToHashSet();
        return Ok(DrApiClient.Search(catalog, query).Take(50).Select(t => new
        {
            t.Id,
            t.Title,
            Kind = t.Kind.ToString(),
            t.Year,
            t.Description,
            t.PosterUrl,
            Followed = followed.Contains(t.Id),
            Downloaded = _service.State.Read(s => s.Items.TryGetValue(t.Id, out var e) && LibraryWriter.FindVideo(e.BasePath) is not null),
        }));
    }

    /// <summary>Lists the seasons and episodes of a series.</summary>
    [HttpGet("Series/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public async Task<ActionResult<object>> Series([FromRoute] string id, CancellationToken ct)
    {
        var s = await _service.Api.GetSeriesAsync(id, ct).ConfigureAwait(false);
        if (s is null)
        {
            return NotFound();
        }

        var (items, queue) = _service.State.Read(st => (st.Items, st.Queue.ToList()));
        string EpisodeState(string epId)
        {
            if (items.TryGetValue(epId, out var e) && LibraryWriter.FindVideo(e.BasePath) is not null)
            {
                return "Downloaded";
            }

            var job = queue.LastOrDefault(j => j.DrId == epId);
            return job?.Status.ToString() ?? string.Empty;
        }

        return Ok(new
        {
            s.Title.Id,
            s.Title.Title,
            Followed = DrtvService.Config.FollowedShows.Any(f => f.ShowId == id),
            Seasons = s.Seasons.Select(se => new
            {
                se.Id,
                se.Title,
                se.SeasonNumber,
                Episodes = se.Episodes.Select(e => new { e.Id, e.EpisodeNumber, e.EpisodeName, State = EpisodeState(e.Id) }),
            }),
        });
    }

    /// <summary>Downloads an episode, season, series, film or program.</summary>
    [HttpPost("Download/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> Download([FromRoute] string id, CancellationToken ct)
    {
        try
        {
            var n = await _service.DownloadAsync(id, ct).ConfigureAwait(false);
            return Ok(new { Queued = n });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Follows a series: new episodes are downloaded automatically.</summary>
    [HttpPost("Follow/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> Follow([FromRoute] string id, [FromQuery] bool downloadExisting, CancellationToken ct)
    {
        try
        {
            var n = await _service.FollowAsync(id, downloadExisting, ct).ConfigureAwait(false);
            return Ok(new { Queued = n });
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Stops following a series (downloaded episodes stay).</summary>
    [HttpDelete("Follow/{id}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Unfollow([FromRoute] string id)
    {
        _service.Unfollow(id);
        return NoContent();
    }

    /// <summary>The download queue, newest first.</summary>
    [HttpGet("Queue")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Produces(MediaTypeNames.Application.Json)]
    public ActionResult<object> Queue()
        => Ok(_service.State.Read(s => s.Queue.OrderBy(j => j.Status == DownloadStatus.Downloading ? 0 : j.Status == DownloadStatus.Queued ? 1 : 2)
            .ThenByDescending(j => j.Finished ?? j.Added).Take(300).Select(j => new
            {
                j.JobId,
                j.DrId,
                j.Title,
                Status = j.Status.ToString(),
                j.Progress,
                j.Message,
                j.Added,
                j.Finished,
            }).ToList()));

    /// <summary>Retries a failed download.</summary>
    [HttpPost("Queue/{jobId}/Retry")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult Retry([FromRoute] string jobId)
    {
        _service.State.Update(s =>
        {
            var j = s.Queue.FirstOrDefault(x => x.JobId == jobId);
            if (j is not null && j.Status is DownloadStatus.Failed)
            {
                j.Status = DownloadStatus.Queued;
                j.Progress = 0;
                j.Message = null;
                j.Finished = null;
            }
        });
        _service.QueueSignal.Release();
        return NoContent();
    }

    /// <summary>Removes a queued (not running) job, or a finished one from the list.</summary>
    [HttpDelete("Queue/{jobId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult RemoveJob([FromRoute] string jobId)
    {
        _service.State.Update(s => s.Queue.RemoveAll(x => x.JobId == jobId && x.Status != DownloadStatus.Downloading));
        return NoContent();
    }

    /// <summary>Clears finished, failed and skipped jobs from the list.</summary>
    [HttpPost("Queue/ClearFinished")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult ClearFinished()
    {
        _service.State.Update(s => s.Queue.RemoveAll(x => x.Status is DownloadStatus.Done or DownloadStatus.Failed or DownloadStatus.Skipped));
        return NoContent();
    }
}
