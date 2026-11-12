using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyDRTV.Configuration;

/// <summary>
/// A series the user follows: new episodes are downloaded automatically.
/// </summary>
public class FollowedShow
{
    public string ShowId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the episode ids that existed when following started and should not be downloaded
    /// (empty when the user chose to download all existing episodes too).
    /// </summary>
    public List<string> IgnoredEpisodeIds { get; set; } = [];
}

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the folder where the DRTV library is written (Serier, Film, Programmer).
    /// </summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address Jellyfin uses to reach itself, written into the .strm files.
    /// Empty = detected automatically from Jellyfin's own port and base URL.
    /// </summary>
    public string ServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path to yt-dlp. Empty = the plugin downloads and updates its own copy.
    /// </summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether favouriting a DRTV item in Jellyfin downloads it
    /// (series: follow + download all).
    /// </summary>
    public bool FavoriteDownloads { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether episode thumbnails are fetched during sync.
    /// </summary>
    public bool DownloadEpisodeThumbnails { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of parallel requests to DR during sync.
    /// </summary>
    public int SyncParallelism { get; set; } = 6;

    /// <summary>
    /// Gets or sets the followed series.
    /// </summary>
    public List<FollowedShow> FollowedShows { get; set; } = [];
}
