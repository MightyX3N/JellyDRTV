namespace Jellyfin.Plugin.JellyDRTV.Dr;

/// <summary>Kind of top-level catalog entry.</summary>
public enum DrKind
{
    Series,
    Movie,
    Program
}

/// <summary>A top-level title in the DRTV catalog (a series, a film or a single program).</summary>
public class DrTitle
{
    public string Id { get; set; } = string.Empty;

    public DrKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string Description { get; set; } = string.Empty;

    public string? PosterUrl { get; set; }

    public string? FanartUrl { get; set; }

    public List<string> Genres { get; set; } = [];

    public List<string> Categories { get; set; } = [];

    public int? DurationSeconds { get; set; }

    public DateTime? Aired { get; set; }
}

/// <summary>A season of a series.</summary>
public class DrSeason
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public int SeasonNumber { get; set; }

    public string? PosterUrl { get; set; }

    public List<DrEpisode> Episodes { get; set; } = [];
}

/// <summary>An episode.</summary>
public class DrEpisode
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string EpisodeName { get; set; } = string.Empty;

    public int EpisodeNumber { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int? DurationSeconds { get; set; }

    public string? ThumbUrl { get; set; }

    public DateTime? Aired { get; set; }

    public bool Available { get; set; } = true;
}

/// <summary>A series with all its seasons and episodes.</summary>
public class DrSeries
{
    public DrTitle Title { get; set; } = new();

    public List<DrSeason> Seasons { get; set; } = [];
}

/// <summary>Resolved playback info for one item.</summary>
public class DrStream
{
    public string Url { get; set; } = string.Empty;

    public string Drm { get; set; } = "None";

    public bool IsDrmProtected => !string.Equals(Drm, "None", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Drm);

    public List<DrSubtitle> Subtitles { get; set; } = [];
}

/// <summary>A subtitle track.</summary>
public class DrSubtitle
{
    public string Url { get; set; } = string.Empty;

    public string Language { get; set; } = string.Empty;
}
