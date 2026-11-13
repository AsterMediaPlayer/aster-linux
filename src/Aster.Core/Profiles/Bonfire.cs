using System.Text.Json;
using System.Text.Json.Serialization;
using Aster.Core.Jellyfin;

namespace Aster.Core.Profiles;

/// <summary>
/// The Bonfire (JellyProfiles) plugin's routes: a household's profiles,
/// switching to one (with its PIN), making a new one, and the household's
/// preferences. The plugin answers at /plugins/profiles on the server, with
/// the ordinary sign-in header; a server without it answers 404. The Apple
/// app's BonfireClient.
/// </summary>
public static class Bonfire
{
    public const string BasePath = "/plugins/profiles";
    public const string PluginGuid = "b1462fca-774b-4b13-8d02-e2d4f2bc18b9";

    /// <summary>Bonfire's colour for a tile without one of its own.</summary>
    public const string DefaultAvatarColor = "#00A4DC";

    /// <summary>From 1.9: a code naming what went wrong, beside the plain-text body.</summary>
    public const string ErrorHeader = "X-Bonfire-Error";

    /// <summary>A profile's name: up to this many of letters, numbers, spaces and - ' . _ @ + (the plugin's own rule).</summary>
    public const int MaxProfileNameLength = 32;

    /// <summary>A PIN: four to eight digits.</summary>
    public const int MinPinLength = 4, MaxPinLength = 8;

    /// <summary>
    /// The plugin writes its keys in camelCase in its own pages and
    /// PascalCase through Jellyfin's default serializer: either reads.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>What's sent: camelCase, anything null left out (the plugin reads keys without regard to case).</summary>
    public static readonly JsonSerializerOptions Sending = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One entry of the household: the main account, a profile, or a member of a linked household (<see cref="IsBonfire"/>).</summary>
public sealed record BonfireProfileRecord
{
    /// <summary>The profile's own Jellyfin user, as a dashed GUID.</summary>
    public string ProfileUserId { get; init; } = "";
    public string ProfileName { get; init; } = "";
    public string? AvatarInitial { get; init; }
    public string? AvatarColor { get; init; }
    public bool? TransparentAvatar { get; init; }

    /// <summary>Whether a PIN is asked for right now (false on the home network when the profile skips it there).</summary>
    public bool? RequiresPin { get; init; }

    /// <summary>Whether a PIN is set at all.</summary>
    public bool? HasPin { get; init; }

    public bool? IsMaster { get; init; }
    public bool? IsAdministrator { get; init; }

    /// <summary>Belongs to another household linked to this one.</summary>
    public bool? IsBonfire { get; init; }

    /// <summary>Null, one of the plugin's avatars, a URL, or a stored picture.</summary>
    public string? ProfileImage { get; init; }

    public string? MasterUserId { get; init; }
    public int? LockoutMinutes { get; init; }
    public bool? BypassPinOnLocalNetwork { get; init; }

    /// <summary>On the main account's entry: how many profiles it may have.</summary>
    public int? MaxSubProfiles { get; init; }
}

/// <summary>The household's switcher preferences, read from the main account.</summary>
public sealed record BonfirePreferences
{
    /// <summary>Ask who's watching when the app opens.</summary>
    public bool? AskOnStartup { get; init; }
    public string? SwitcherLocation { get; init; }
    public string? SwitcherMode { get; init; }
    public string? MasterUserId { get; init; }
}

/// <summary>A switch that went through: the profile's own sign-in.</summary>
public sealed record BonfireSwitchResult
{
    public string ActiveProfileToken { get; init; } = "";
    public string JellyfinUserId { get; init; } = "";
}

/// <summary>What the plugin says about itself before anyone signs in (from 1.9): its version, whether it's off, which app features are on, maintenance.</summary>
public sealed record BonfireInfo
{
    public sealed record FeatureSet
    {
        /// <summary>Profiles sign in with their PIN where an app asks for a password.</summary>
        public bool? ClientPinLogin { get; init; }

        /// <summary>/Users/Public lists the household's profiles on a known device.</summary>
        public bool? ClientProfileList { get; init; }

        public bool? RequireMainPin { get; init; }
    }

    public sealed record MaintenanceWindow
    {
        public string? Message { get; init; }
        public string? UntilUtc { get; init; }
    }

    public string? Version { get; init; }
    public int? ApiVersion { get; init; }
    public bool? Disabled { get; init; }
    public FeatureSet? Features { get; init; }
    public MaintenanceWindow? Maintenance { get; init; }
}

/// <summary>A library the main account can see, which a profile may be given.</summary>
public sealed record BonfireLibrary
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string? CollectionType { get; init; }
}

/// <summary>One step of the server's rating system: an age, and the labels that mean it ("12", "12A").</summary>
public sealed record BonfireRatingChoice
{
    public int Score { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
}

public sealed record BonfireRatings
{
    public string? Country { get; init; }
    public IReadOnlyList<BonfireRatingChoice> Ratings { get; init; } = [];
}

/// <summary>A picture to pick for a profile: one the administrator uploaded, or one of the plugin's own set.</summary>
public sealed record BonfireAvatar
{
    public string Id { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Url { get; init; }
    public string? ThumbUrl { get; init; }
    public bool? IsStarter { get; init; }
}

public sealed record BonfireAvatarLibrary
{
    public bool? AllowCustomUploads { get; init; }
    public bool? StarterAvatarsEnabled { get; init; }
    public IReadOnlyList<BonfireAvatar> Avatars { get; init; } = [];
}

/// <summary>A new profile, as the plugin takes it; anything null is left out.</summary>
public sealed record BonfireCreateRequest
{
    public required string ProfileName { get; init; }
    public string? Pin { get; init; }
    public required string AvatarColor { get; init; }

    /// <summary>The rating's score, as text.</summary>
    public string? MaxParentalRating { get; init; }

    /// <summary>Null for every library the main account has, now and later.</summary>
    public IReadOnlyList<string>? EnabledFolders { get; init; }

    public string? MasterPin { get; init; }

    /// <summary>One of the plugin's avatars, copied to the profile.</summary>
    public string? AvatarLibraryId { get; init; }

    public bool? BypassPinOnLocalNetwork { get; init; }

    public static BonfireCreateRequest From(ProfileDraft draft)
    {
        var pin = draft.Pin.Length == 0 ? null : draft.Pin;
        return new BonfireCreateRequest
        {
            ProfileName = draft.TrimmedName,
            Pin = pin,
            AvatarColor = draft.ColorHex,
            MaxParentalRating = draft.RatingScore?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EnabledFolders = draft.LibraryIds?.Order(StringComparer.Ordinal).ToList(),
            MasterPin = draft.MasterPin.Length == 0 ? null : draft.MasterPin,
            AvatarLibraryId = draft.AvatarId,
            BypassPinOnLocalNetwork = pin is null ? null : draft.BypassPinOnLocalNetwork,
        };
    }
}

public sealed record BonfireCreatedProfile
{
    public string ProfileUserId { get; init; } = "";
    public string ProfileName { get; init; } = "";
}

public sealed class BonfireClient(JellyfinClient jellyfin)
{
    public JellyfinClient Jellyfin { get; } = jellyfin;

    private sealed record SwitchBody(string ProfileId, string? Pin);

    /// <summary>What the server answered: the body, the status, and (from 1.9) the plugin's own code for what went wrong.</summary>
    private sealed record Answer(byte[] Data, int Status, string? Code);

    /// <summary>Every profile the signed-in person can switch to, or null when the server has no plugin (the route answers 404).</summary>
    public async Task<IReadOnlyList<BonfireProfileRecord>?> ListAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync("/list", ct: ct).ConfigureAwait(false);
        if (answer.Status == 404) return null;
        Check(answer, pinSent: false);
        return Decode<List<BonfireProfileRecord>>(answer);
    }

    public async Task<BonfirePreferences> PreferencesAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync("/preferences", ct: ct).ConfigureAwait(false);
        Check(answer, pinSent: false);
        return Decode<BonfirePreferences>(answer);
    }

    /// <summary>The plugin's description of itself; null on a version without the route (before 1.9 it answers 404 or 405).</summary>
    public async Task<BonfireInfo?> InfoAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync("/info", ct: ct).ConfigureAwait(false);
        if (answer.Status is 404 or 405) return null;
        Check(answer, pinSent: false);
        return Decode<BonfireInfo>(answer);
    }

    /// <summary>
    /// Switches to a profile, with its PIN where one is asked for: a token for
    /// the profile's own user. The server ends that user's earlier sign-ins
    /// from this device first; the one making the call stays.
    /// </summary>
    public async Task<BonfireSwitchResult> SwitchProfileAsync(string id, string? pin, CancellationToken ct = default)
    {
        pin = string.IsNullOrEmpty(pin) ? null : pin;
        var answer = await SendAsync("/switch", HttpMethod.Post, new SwitchBody(id, pin), ct).ConfigureAwait(false);
        Check(answer, pinSent: pin is not null);
        return Decode<BonfireSwitchResult>(answer);
    }

    /// <summary>Checks a profile's PIN without switching to it.</summary>
    public async Task VerifyPinAsync(string id, string pin, CancellationToken ct = default)
    {
        var answer = await SendAsync("/verify-pin", HttpMethod.Post, new SwitchBody(id, pin), ct).ConfigureAwait(false);
        Check(answer, pinSent: true);
    }

    // ---- Making a profile ----

    /// <summary>The libraries the signed-in account can see.</summary>
    public async Task<IReadOnlyList<BonfireLibrary>> LibrariesAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync("/libraries", ct: ct).ConfigureAwait(false);
        Check(answer, pinSent: false);
        return Decode<List<BonfireLibrary>>(answer);
    }

    /// <summary>The rating limits the server offers, in its metadata country.</summary>
    public async Task<BonfireRatings> RatingsAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync("/ratings", ct: ct).ConfigureAwait(false);
        Check(answer, pinSent: false);
        return Decode<BonfireRatings>(answer);
    }

    /// <summary>The pictures a profile may pick from.</summary>
    public async Task<BonfireAvatarLibrary> AvatarsAsync(CancellationToken ct = default)
    {
        var answer = await SendAsync("/avatars", ct: ct).ConfigureAwait(false);
        Check(answer, pinSent: false);
        return Decode<BonfireAvatarLibrary>(answer);
    }

    /// <summary>Makes a profile under the signed-in main account. The server's refusals come back as <see cref="ProfileSwitchKind.Failed"/> with its words.</summary>
    public async Task<BonfireCreatedProfile> CreateProfileAsync(BonfireCreateRequest request, CancellationToken ct = default)
    {
        var answer = await SendAsync("/create", HttpMethod.Post, request, ct).ConfigureAwait(false);
        Check(answer, pinSent: false);
        return Decode<BonfireCreatedProfile>(answer);
    }

    /// <summary>A profile's stored picture, small; 404 without one. Anyone can read it.</summary>
    public Uri ImageUrl(string profileUserId) => Jellyfin.Url(Bonfire.BasePath + "/image/" + profileUserId, new() { ["size"] = "thumb" });

    /// <summary>One of the plugin's avatars (its own set, or the administrator's), small. Anyone can read it.</summary>
    public Uri AvatarUrl(string id) => Jellyfin.Url(Bonfire.BasePath + "/avatars/" + id, new() { ["size"] = "thumb" });

    private async Task<Answer> SendAsync(string path, HttpMethod? method = null, object? body = null, CancellationToken ct = default)
    {
        var json = body is null ? null : JsonSerializer.Serialize(body, body.GetType(), Bonfire.Sending);
        var (status, data, code) = await Jellyfin.SendForAnswerAsync(method ?? HttpMethod.Get, Bonfire.BasePath + path, json, Bonfire.ErrorHeader,
            TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        return new Answer(data, status, code?.ToLowerInvariant());
    }

    private static T Decode<T>(Answer answer)
    {
        try { return JsonSerializer.Deserialize<T>(answer.Data, Bonfire.Json) ?? throw new ProfileSwitchException(ProfileSwitchKind.Failed, "The server's answer was empty."); }
        catch (JsonException error) { throw new ProfileSwitchException(ProfileSwitchKind.Failed, $"The server's answer couldn't be read ({error.Message})."); }
    }

    /// <summary>
    /// The plugin's refusals. From 1.9 a header names them; before, the
    /// status has to do: a wrong PIN is a 400 when one was sent, too many
    /// tries a 429. The body is plain text written for the person.
    /// </summary>
    private static void Check(Answer answer, bool pinSent)
    {
        if (answer.Status is >= 200 and < 300) return;
        var trimmed = System.Text.Encoding.UTF8.GetString(answer.Data, 0, Math.Min(answer.Data.Length, 300)).Trim();
        var text = trimmed.Length > 0 ? trimmed : null;
        throw answer.Code switch
        {
            "pin_wrong" => new ProfileSwitchException(ProfileSwitchKind.WrongPin),
            "pin_missing" or "main_pin_required" => new ProfileSwitchException(ProfileSwitchKind.PinNeeded),
            "daily_limit_reached" or "rate_limited" or "too_many_attempts" => new ProfileSwitchException(ProfileSwitchKind.RateLimited),
            "maintenance" => new ProfileSwitchException(ProfileSwitchKind.Failed, text ?? "The server is closed for maintenance."),
            { Length: > 0 } code => new ProfileSwitchException(ProfileSwitchKind.Failed, text ?? $"The server didn't allow it ({code})."),
            _ => answer.Status switch
            {
                400 when pinSent => new ProfileSwitchException(ProfileSwitchKind.WrongPin),
                429 => new ProfileSwitchException(ProfileSwitchKind.RateLimited),
                401 or 403 => new ProfileSwitchException(ProfileSwitchKind.Failed, text ?? "The server didn't allow it."),
                _ => new ProfileSwitchException(ProfileSwitchKind.Failed, text ?? $"The server answered {answer.Status}."),
            },
        };
    }
}
