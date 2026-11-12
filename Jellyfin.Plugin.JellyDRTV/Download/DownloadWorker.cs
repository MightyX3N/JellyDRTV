using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.JellyDRTV.Library;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyDRTV.Download;

/// <summary>
/// Background worker: works through the download queue one item at a time with yt-dlp,
/// and turns favourites on DRTV items into downloads.
/// </summary>
public partial class DownloadWorker : BackgroundService
{
    private readonly DrtvService _service;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<DownloadWorker> _logger;
    private readonly YtDlp _ytDlp;

    public DownloadWorker(
        DrtvService service,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IMediaEncoder mediaEncoder,
        IApplicationPaths paths,
        ILogger<DownloadWorker> logger)
    {
        _service = service;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _ytDlp = new YtDlp(Path.Combine(paths.PluginConfigurationsPath, "JellyDRTV", "tools"), logger);
        Instance = this;
    }

    public static DownloadWorker? Instance { get; private set; }

    public YtDlp YtDlp => _ytDlp;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged += OnConfigurationChanged;
        }

        // Point the .strm files at Jellyfin's current address (port or base URL may have changed),
        // and add (or remove) DRTV in Live TV to match the setting.
        _ = Task.Run(
            async () =>
            {
                FixStrmAddresses();
                await _service.ApplyLiveTvAsync(waitForServer: true).ConfigureAwait(false);
            },
            cancellationToken);
        return base.StartAsync(cancellationToken);
    }

    private void OnConfigurationChanged(object? sender, MediaBrowser.Model.Plugins.BasePluginConfiguration e)
        => _ = Task.Run(async () =>
        {
            FixStrmAddresses();
            await _service.ApplyLiveTvAsync().ConfigureAwait(false);
        });

    private void FixStrmAddresses()
    {
        try
        {
            _service.UpdateStrmAddressesIfNeeded();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyDRTV: could not update .strm addresses");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        if (Plugin.Instance is not null)
        {
            Plugin.Instance.ConfigurationChanged -= OnConfigurationChanged;
        }

        return base.StopAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var job = _service.State.Read(s => s.Queue.FirstOrDefault(j => j.Status == DownloadStatus.Queued));
            if (job is null)
            {
                try
                {
                    await _service.QueueSignal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                await ProcessAsync(job.JobId, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "JellyDRTV: download of {Title} failed", job.Title);
                SetJob(job.JobId, j =>
                {
                    j.Status = DownloadStatus.Failed;
                    j.Message = ex.Message;
                    j.Finished = DateTime.UtcNow;
                });
            }
        }
    }

    private void SetJob(string jobId, Action<DownloadJob> change)
        => _service.State.Update(s =>
        {
            var j = s.Queue.FirstOrDefault(x => x.JobId == jobId);
            if (j is not null)
            {
                change(j);
            }
        });

    private async Task ProcessAsync(string jobId, CancellationToken ct)
    {
        var (job, entry) = _service.State.Read(s =>
        {
            var j = s.Queue.First(x => x.JobId == jobId);
            s.Items.TryGetValue(j.DrId, out var e);
            return (j, e);
        });

        void Finish(DownloadStatus status, string? message) => SetJob(jobId, j =>
        {
            j.Status = status;
            j.Message = message;
            j.Finished = DateTime.UtcNow;
            if (status == DownloadStatus.Done)
            {
                j.Progress = 100;
            }
        });

        if (entry is null)
        {
            Finish(DownloadStatus.Failed, "This item is no longer in the DRTV library.");
            return;
        }

        if (LibraryWriter.FindVideo(entry.BasePath) is not null)
        {
            Finish(DownloadStatus.Done, "Already downloaded.");
            return;
        }

        SetJob(jobId, j =>
        {
            j.Status = DownloadStatus.Downloading;
            j.Progress = 0;
            j.Message = "Checking stream";
        });

        var stream = await _service.Api.GetStreamAsync(entry.DrId, ct).ConfigureAwait(false);
        if (stream is null)
        {
            Finish(DownloadStatus.Failed, "DR returned no stream. It may have been removed, or this server is outside Denmark.");
            return;
        }

        if (stream.IsDrmProtected)
        {
            Finish(DownloadStatus.Skipped, $"DRM-protected ({stream.Drm}); it can't be downloaded.");
            return;
        }

        SetJob(jobId, j => j.Message = "Preparing yt-dlp");
        var exe = await _ytDlp.GetExecutableAsync(DrtvService.Config.YtDlpPath, ct).ConfigureAwait(false);

        // yt-dlp gets DR's HLS playlist directly (resolved by the plugin) rather than the dr.dk page,
        // so a change on DR's website can't break downloads as long as the stream API works.
        var args = new List<string>
        {
            stream.Url,
            "-o", entry.BasePath + ".%(ext)s",
            "-f", "bv*+ba/b",
            "--merge-output-format", "mkv",
            "--no-playlist", "--newline", "--no-mtime", "--no-overwrites", "--no-part",
            "--retries", "10", "--fragment-retries", "10",
            "--concurrent-fragments", "4",
        };
        var ffmpeg = _mediaEncoder.EncoderPath;
        if (!string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg))
        {
            args.Add("--ffmpeg-location");
            args.Add(ffmpeg);
        }

        SetJob(jobId, j => j.Message = "Downloading");
        var lastReport = DateTime.MinValue;
        var (code, output) = await YtDlp.RunAsync(exe, args, line =>
        {
            var m = ProgressLine().Match(line);
            if (m.Success && DateTime.UtcNow - lastReport > TimeSpan.FromSeconds(2)
                && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                lastReport = DateTime.UtcNow;
                SetJob(jobId, j => j.Progress = Math.Round(pct, 1));
            }
        }, ct).ConfigureAwait(false);

        var video = LibraryWriter.FindVideo(entry.BasePath);
        if (code != 0 || video is null)
        {
            CleanupPartials(entry.BasePath);
            var reason = output.Split('\n').LastOrDefault(l => l.Contains("ERROR", StringComparison.Ordinal)) ?? $"yt-dlp exited with code {code}";
            if (reason.Contains("403", StringComparison.Ordinal))
            {
                reason = "DR blocked the stream (HTTP 403). This title is limited to Denmark, so the Jellyfin server has to be in Denmark.";
            }

            Finish(DownloadStatus.Failed, reason.Trim());
            return;
        }

        SetJob(jobId, j => j.Message = "Fetching subtitles");
        await _service.WriteSubtitlesAsync(entry, stream, ct).ConfigureAwait(false);

        // The video replaces the placeholder.
        try
        {
            File.Delete(entry.BasePath + ".strm");
        }
        catch (IOException)
        {
        }

        Finish(DownloadStatus.Done, Path.GetFileName(video));
        _logger.LogInformation("JellyDRTV: downloaded {Title} to {File}", entry.Title, video);
        _service.NotifyChanged(video);
        _service.NotifyChanged(entry.BasePath + ".strm");
    }

    private static void CleanupPartials(string basePath)
    {
        var dir = Path.GetDirectoryName(basePath)!;
        var name = Path.GetFileName(basePath);
        foreach (var f in Directory.GetFiles(dir, name + ".*"))
        {
            if (f.EndsWith(".part", StringComparison.Ordinal) || f.Contains(".part-Frag", StringComparison.Ordinal) || f.EndsWith(".ytdl", StringComparison.Ordinal)
                || Regex.IsMatch(Path.GetFileName(f), @"\.f\d+\.\w+$"))
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    [GeneratedRegex(@"\[download\]\s+([\d.]+)%")]
    private static partial Regex ProgressLine();

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e.SaveReason != UserDataSaveReason.UpdateUserRating || e.UserData is null || !e.UserData.IsFavorite || e.Item is null)
        {
            return;
        }

        var cfg = DrtvService.Config;
        if (!cfg.FavoriteDownloads || string.IsNullOrEmpty(cfg.LibraryPath) || string.IsNullOrEmpty(e.Item.Path))
        {
            return;
        }

        var root = Path.GetFullPath(cfg.LibraryPath);
        if (!Path.GetFullPath(e.Item.Path).StartsWith(root, StringComparison.Ordinal))
        {
            return;
        }

        var item = e.Item;
        _ = Task.Run(async () =>
        {
            try
            {
                var (id, kind) = _service.Lookup(item.Path);
                if (id is null)
                {
                    return;
                }

                if (item is Series)
                {
                    var n = await _service.FollowAsync(id, true, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogInformation("JellyDRTV: favourite → following {Title}, {Count} episodes queued", item.Name, n);
                }
                else if (kind == "group")
                {
                    var n = _service.EnqueueGroup(id);
                    _logger.LogInformation("JellyDRTV: favourite → {Count} episodes of {Title} queued", n, item.Name);
                }
                else
                {
                    _service.Enqueue(id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyDRTV: could not handle favourite on {Path}", item.Path);
            }
        });
    }
}
