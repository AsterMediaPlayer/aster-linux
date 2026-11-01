using System.Text.Json.Serialization;

namespace Aster.Core.Seerr;

// Seerr (the joined Overseerr and Jellyseerr): the viewer's own request
// server, asked for films and shows the media server hasn't got. Ported
// from the Apple app's AsterFIN/Seerr (SeerrClient.swift, SeerrDiscover.swift).

/// <summary>Where a title stands with Seerr (its MediaStatus).</summary>
public enum SeerrStatus
{
    Unknown = 1,
    Pending,
    Processing,
    PartiallyAvailable,
    Available,
    Blacklisted,
    Deleted,
}

public static class SeerrStatusText
{
    /// <summary>What its badge says; null for a title nobody's asked for.</summary>
    public static string? Label(this SeerrStatus status) => status switch
    {
        SeerrStatus.Pending => "Requested",
        SeerrStatus.Processing => "Processing",
        SeerrStatus.PartiallyAvailable => "Partly Available",
        SeerrStatus.Available => "Available",
        SeerrStatus.Blacklisted => "Not Available",
        _ => null,
    };

    /// <summary>Whether there's anything left to ask for.</summary>
    public static bool CanRequest(this SeerrStatus status) =>
        status is SeerrStatus.Unknown or SeerrStatus.Deleted or SeerrStatus.PartiallyAvailable;
}

/// <summary>A film or show as Seerr lists it (from TMDB).</summary>
public sealed record SeerrTitle
{
    public int Id { get; init; }
    /// <summary>"movie" or "tv"; Discover's rows of one kind may leave it out.</summary>
    public string? MediaType { get; init; }
    public string? Title { get; init; }
    public string? Name { get; init; }
    public string? ReleaseDate { get; init; }
    public string? FirstAirDate { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public string? Overview { get; init; }
    public SeerrMediaInfo? MediaInfo { get; init; }

    public bool IsMovie => MediaType == "movie";
    public string DisplayTitle => Title ?? Name ?? "";
    public string? Year => (ReleaseDate ?? FirstAirDate) is { Length: >= 4 } date ? date[..4] : null;
    public SeerrStatus Status => MediaInfo?.Status ?? SeerrStatus.Unknown;
    public string? PosterUrl => SeerrClient.Image(PosterPath, "w342");
    public string? BackdropUrl => SeerrClient.Image(BackdropPath, "w780");
    /// <summary>Unique across films and shows (TMDB numbers each on its own).</summary>
    public string Key => $"{MediaType ?? "tv"}-{Id}";
}

public sealed record SeerrMediaInfo
{
    public SeerrStatus? Status { get; init; }
    public IReadOnlyList<SeerrSeasonStatus>? Seasons { get; init; }
    public string? JellyfinMediaId { get; init; }
    public string? RatingKey { get; init; }
}

public sealed record SeerrSeasonStatus(int SeasonNumber, SeerrStatus? Status);

/// <summary>A show's seasons, and where each stands.</summary>
public sealed record SeerrShow
{
    public IReadOnlyList<SeerrSeason> Seasons { get; init; } = [];
    public SeerrMediaInfo? MediaInfo { get; init; }

    /// <summary>The seasons (not the specials) and their status, requests included.</summary>
    public IReadOnlyList<(int Number, int? Episodes, SeerrStatus Status)> SeasonStatus()
    {
        var known = new Dictionary<int, SeerrStatus>();
        foreach (var season in MediaInfo?.Seasons ?? []) known.TryAdd(season.SeasonNumber, season.Status ?? SeerrStatus.Unknown);
        return Seasons.Where(s => s.SeasonNumber > 0)
            .Select(s => (s.SeasonNumber, s.EpisodeCount, known.TryGetValue(s.SeasonNumber, out var status) ? status : SeerrStatus.Unknown))
            .ToList();
    }
}

public sealed record SeerrSeason(int SeasonNumber, int? EpisodeCount, string? Name);

/// <summary>Signed in to Seerr: its address, the session, and who. Synced with the Apple and Android apps.</summary>
public sealed record SeerrAccount(Uri Url, string Cookie, string Name, int UserId);

public sealed class SeerrException(SeerrFailure failure, string? message = null, int status = 0) : Exception(message ?? Describe(failure, status))
{
    public SeerrFailure Failure { get; } = failure;
    public int Status { get; } = status;

    private static string Describe(SeerrFailure failure, int status) => failure switch
    {
        SeerrFailure.SignedOut => "The Seerr sign-in has run out. Sign in again in Settings.",
        SeerrFailure.Refused => "Seerr turned that down.",
        _ => $"Seerr answered {status}.",
    };
}

public enum SeerrFailure { SignedOut, Refused, Http }

/// <summary>A page of anything Seerr pages.</summary>
public sealed record SeerrPage<T>
{
    public int? Page { get; init; }
    public int? TotalPages { get; init; }
    public int? TotalResults { get; init; }
    public IReadOnlyList<T> Results { get; init; } = [];
}

/// <summary>One of Seerr's Discover rows, as its admin arranged them.</summary>
public sealed record SeerrSlider
{
    public int? Id { get; init; }
    public int Type { get; init; }
    public string? Title { get; init; }
    public string? Data { get; init; }
    public bool? Enabled { get; init; }
}

/// <summary>The signed-in Seerr user: who, and whether they may manage requests.</summary>
public sealed record SeerrUser
{
    public int Id { get; init; }
    public string? DisplayName { get; init; }
    public int? Permissions { get; init; }

    /// <summary>Seerr's ADMIN (2) or MANAGE_REQUESTS (16) permission.</summary>
    public bool ManagesRequests => ((Permissions ?? 0) & (2 | 16)) != 0;
}

/// <summary>A request, as Seerr lists them.</summary>
public sealed record SeerrRequest
{
    public int Id { get; init; }
    /// <summary>1 pending approval, 2 approved, 3 declined, 4 failed, 5 completed.</summary>
    public int Status { get; init; }
    public string? Type { get; init; }
    public string? CreatedAt { get; init; }
    public bool? Is4k { get; init; }
    public SeerrRequestMedia Media { get; init; } = new();
    public IReadOnlyList<SeerrRequestSeason>? Seasons { get; init; }
    public SeerrPerson? RequestedBy { get; init; }
    public SeerrPerson? ModifiedBy { get; init; }
    public int? ServerId { get; init; }
    public int? ProfileId { get; init; }
    public string? RootFolder { get; init; }
    public int? LanguageProfileId { get; init; }

    public bool IsPending => Status == 1;

    /// <summary>Where the request stands, as Seerr's own list says it.</summary>
    public string StateLabel => Status switch
    {
        1 => "Pending Approval",
        3 => "Declined",
        4 => "Failed",
        _ => Media.Status switch
        {
            SeerrStatus.Available => "Available",
            SeerrStatus.PartiallyAvailable => "Partly Available",
            SeerrStatus.Processing => "Processing",
            _ => Status == 5 ? "Completed" : "Approved",
        },
    };

    public DateTimeOffset? Created => DateTimeOffset.TryParse(CreatedAt, System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.AssumeUniversal, out var when) ? when : null;
}

public sealed record SeerrRequestMedia
{
    public int TmdbId { get; init; }
    public string? MediaType { get; init; }
    public SeerrStatus? Status { get; init; }
}

public sealed record SeerrRequestSeason(int SeasonNumber);

public sealed record SeerrPerson(int Id, string? DisplayName);

/// <summary>A genre with pictures from its titles, for Discover's genre rows.</summary>
public sealed record SeerrGenre(int Id, string Name, IReadOnlyList<string>? Backdrops);

/// <summary>A film studio or TV network, for Discover's rows of them.</summary>
public sealed record SeerrCompany(int Id, string Name, string? LogoPath);

/// <summary>A streaming service, to filter by what's on it.</summary>
public sealed record SeerrProvider(int Id, string Name, string? LogoPath, int? DisplayPriority);

/// <summary>Films or shows: Discover's two kinds.</summary>
public enum SeerrKind
{
    [JsonStringEnumMemberName("movie")] Movie,
    [JsonStringEnumMemberName("tv")] Tv,
}

/// <summary>Which requests to list, as Seerr's own filter names them.</summary>
public enum SeerrRequestFilter { All, Pending, Approved, Processing, Available, Unavailable, Failed }

/// <summary>A film's or show's page from Seerr (/movie/{id}, /tv/{id}): what the request page shows.</summary>
public sealed record SeerrDetails
{
    public int Id { get; init; }
    public string? Title { get; init; }
    public string? Name { get; init; }
    public string? OriginalTitle { get; init; }
    public string? OriginalName { get; init; }
    public string? Tagline { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public string? ReleaseDate { get; init; }
    public string? FirstAirDate { get; init; }
    public int? Runtime { get; init; }
    public string? Status { get; init; }
    public double? VoteAverage { get; init; }
    public IReadOnlyList<SeerrNamed>? Genres { get; init; }
    public IReadOnlyList<SeerrNamed>? Networks { get; init; }
    public IReadOnlyList<SeerrVideo>? RelatedVideos { get; init; }
    public IReadOnlyList<SeerrSeason>? Seasons { get; init; }
    public SeerrMediaInfo? MediaInfo { get; init; }

    public string DisplayTitle => Title ?? Name ?? "";
    public string? Year => (ReleaseDate ?? FirstAirDate) is { Length: >= 4 } date ? date[..4] : null;

    /// <summary>The trailer to show: YouTube's, a trailer before a teaser (new anime often have only that).</summary>
    public SeerrVideo? Trailer => RelatedVideos?.Where(v => v.Site == "YouTube" && !string.IsNullOrEmpty(v.Key))
        .OrderBy(v => v.Type == "Trailer" ? 0 : v.Type == "Teaser" ? 1 : 2).FirstOrDefault(v => v.Type is "Trailer" or "Teaser");
}

public sealed record SeerrNamed(int Id, string Name);

/// <summary>A video TMDB lists for a title; YouTube's play at youtube.com/watch?v={Key}.</summary>
public sealed record SeerrVideo(string? Site, string? Key, string? Type, string? Name)
{
    public string? Url => Site == "YouTube" && !string.IsNullOrEmpty(Key) ? $"https://www.youtube.com/watch?v={Key}" : null;
}
