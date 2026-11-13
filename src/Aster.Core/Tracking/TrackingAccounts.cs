using System.Globalization;
using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tracking;

/// <summary>A service the server's Aster Discovery and Tracking plugin links and sends to. The Apple app's TrackedService.</summary>
public enum TrackedService
{
    Simkl,
    AniList,
    MyAnimeList,
    LastFm,
    ListenBrainz,
    Hardcover,
}

/// <summary>How linking goes: a code to enter on the service's site, approving Aster there, or a code or token pasted back.</summary>
public enum TrackedLinkFlow { Code, Approve, Paste }

public static class TrackedServices
{
    /// <summary>The name the plugin knows it by: "simkl", "anilist", "myanimelist", "lastfm", "listenbrainz", "hardcover".</summary>
    public static string Key(this TrackedService service) => service.ToString().ToLowerInvariant();

    public static string Title(this TrackedService service) => service switch
    {
        TrackedService.AniList => "AniList",
        TrackedService.MyAnimeList => "MyAnimeList",
        TrackedService.LastFm => "Last.fm",
        TrackedService.ListenBrainz => "ListenBrainz",
        _ => service.ToString(),
    };

    public static TrackedLinkFlow Flow(this TrackedService service) => service switch
    {
        TrackedService.Simkl or TrackedService.Hardcover => TrackedLinkFlow.Code,
        TrackedService.LastFm => TrackedLinkFlow.Approve,
        _ => TrackedLinkFlow.Paste,
    };

    /// <summary>What it keeps track of, as the page groups them.</summary>
    public static string Group(this TrackedService service) => service switch
    {
        TrackedService.Simkl => "Films and Shows",
        TrackedService.AniList or TrackedService.MyAnimeList => "Anime",
        TrackedService.LastFm or TrackedService.ListenBrainz => "Music",
        _ => "Audiobooks",
    };

    /// <summary>"watch" or "listen to", for the switch's words.</summary>
    public static string Verb(this TrackedService service) =>
        service is TrackedService.LastFm or TrackedService.ListenBrainz or TrackedService.Hardcover ? "listen to" : "watch";

    /// <summary>The groups in the page's order.</summary>
    public static IReadOnlyList<string> Groups { get; } = ["Anime", "Films and Shows", "Music", "Audiobooks"];
}

public sealed record TrackedAccount
{
    public bool? Available { get; init; }
    public bool? Linked { get; init; }
    public string? Name { get; init; }
    public string? LinkedAt { get; init; }

    /// <summary>When the link runs out and needs doing again (AniList's year).</summary>
    public string? Expires { get; init; }

    public bool? Scrobble { get; init; }

    public bool IsLinked => Linked == true;
}

public sealed record SendLogEntry
{
    public string? At { get; init; }
    public string? Service { get; init; }
    public string? Title { get; init; }
    public string? Result { get; init; }
}

/// <summary>What's linked on the server for this person, and the last sends.</summary>
public sealed record TrackedAccounts
{
    public TrackedAccount? Simkl { get; init; }
    public TrackedAccount? AniList { get; init; }
    public TrackedAccount? MyAnimeList { get; init; }
    public TrackedAccount? LastFm { get; init; }
    public TrackedAccount? ListenBrainz { get; init; }
    public TrackedAccount? Hardcover { get; init; }

    /// <summary>Sends waiting to be tried again.</summary>
    public int? Waiting { get; init; }

    /// <summary>The last sends, newest first.</summary>
    public List<SendLogEntry>? Log { get; init; }

    public TrackedAccount? Account(TrackedService service) => service switch
    {
        TrackedService.Simkl => Simkl,
        TrackedService.AniList => AniList,
        TrackedService.MyAnimeList => MyAnimeList,
        TrackedService.LastFm => LastFm,
        TrackedService.ListenBrainz => ListenBrainz,
        _ => Hardcover,
    };
}

/// <summary>What starting to link gives: where to go, and what to enter there.</summary>
public sealed record TrackedLink
{
    public string Url { get; init; } = "";
    public string? Code { get; init; }

    /// <summary>The address with the code in it.</summary>
    public string? CompleteUrl { get; init; }

    public bool? Paste { get; init; }

    /// <summary>Seconds between checks.</summary>
    public int? Interval { get; init; }

    public int? ExpiresIn { get; init; }
}

public enum TrackedLinkResult { Linked, Waiting, Expired, Failed }

public sealed class TrackingException(string message) : Exception(message);

/// <summary>
/// Settings ▸ Tracking with the server's plugin: each service linked once on
/// the server, for every app, which sends what's watched or listened to.
/// The Apple app's TrackingAPI.
/// </summary>
public static class TrackingAccountsApi
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static async Task<TrackedAccounts> AccountsAsync(JellyfinClient client, CancellationToken ct = default)
    {
        var (status, body) = await client.SendForStatusAsync(HttpMethod.Get, "/AsterTracking/Accounts", ct: ct).ConfigureAwait(false);
        if (status != 200) throw new TrackingException($"The server answered {status}.");
        return Decode<TrackedAccounts>(body);
    }

    public static async Task<TrackedLink> StartLinkAsync(JellyfinClient client, TrackedService service, CancellationToken ct = default)
    {
        var (status, body) = await client.SendForStatusAsync(HttpMethod.Post, $"/AsterTracking/Accounts/{service.Key()}/Link", ct: ct).ConfigureAwait(false);
        return status switch
        {
            200 => Decode<TrackedLink>(body),
            409 => throw new TrackingException(NoApp(service)),
            502 => throw new TrackingException(Unreachable(service)),
            _ => throw new TrackingException($"The server answered {status}."),
        };
    }

    /// <summary>Whether a code's been entered or approved (code and approve flows), or hands over what was pasted.</summary>
    public static async Task<(TrackedLinkResult Result, string? Message)> CompleteAsync(JellyfinClient client, TrackedService service, string? pasted = null, CancellationToken ct = default)
    {
        object body = pasted is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Code"] = pasted };
        var (status, _) = await client.SendForStatusAsync(HttpMethod.Post, $"/AsterTracking/Accounts/{service.Key()}/Link/Complete", body, ct).ConfigureAwait(false);
        return status switch
        {
            200 => (TrackedLinkResult.Linked, null),
            202 => (TrackedLinkResult.Waiting, null),
            400 => (TrackedLinkResult.Failed, "Paste the code the sign-in page showed."),
            409 => (TrackedLinkResult.Failed, NoApp(service)),
            410 => (TrackedLinkResult.Expired, null),
            502 => (TrackedLinkResult.Failed, Unreachable(service)),
            _ => (TrackedLinkResult.Failed, $"The server answered {status}."),
        };
    }

    public static async Task SetScrobbleAsync(JellyfinClient client, TrackedService service, bool on, CancellationToken ct = default)
    {
        var (status, _) = await client.SendForStatusAsync(HttpMethod.Put, $"/AsterTracking/Accounts/{service.Key()}/Settings",
            new Dictionary<string, bool> { ["Scrobble"] = on }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) throw new TrackingException($"The server answered {status}.");
    }

    public static async Task UnlinkAsync(JellyfinClient client, TrackedService service, CancellationToken ct = default)
    {
        var (status, _) = await client.SendForStatusAsync(HttpMethod.Delete, $"/AsterTracking/Accounts/{service.Key()}", ct: ct).ConfigureAwait(false);
        if (status is < 200 or >= 300) throw new TrackingException($"The server answered {status}.");
    }

    /// <summary>An ISO 8601 date, with or without fractions of a second.</summary>
    public static DateTimeOffset? Date(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    public static TrackedAccounts ReadAccounts(string json) => Decode<TrackedAccounts>(System.Text.Encoding.UTF8.GetBytes(json));

    private static T Decode<T>(byte[] body)
    {
        try { return JsonSerializer.Deserialize<T>(body, Json) ?? throw new TrackingException("The server's answer was empty."); }
        catch (JsonException error) { throw new TrackingException($"The server's answer couldn't be read ({error.Message})."); }
    }

    private static string NoApp(TrackedService service) => $"This server has no app to link {service.Title()} with yet. Its admin can add one in the plugin's settings.";
    private static string Unreachable(TrackedService service) => $"{service.Title()} couldn't be reached. Try again in a moment.";
}
