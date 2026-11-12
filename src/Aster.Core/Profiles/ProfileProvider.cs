using System.Globalization;
using System.Text;
using Aster.Core.Jellyfin;

namespace Aster.Core.Profiles;

/// <summary>
/// One person in the household, as the picker shows them: a Bonfire
/// profile on a Jellyfin server, or a Plex Home user. The Apple app's
/// HouseholdProfile.
/// </summary>
public sealed record HouseholdProfile
{
    /// <summary>The id as Jellyfin writes it either way (lowercase, no dashes), or a Plex Home user's uuid: what the session's user id is compared to.</summary>
    public required string Id { get; init; }

    /// <summary>The id as the server sent it, for its own routes.</summary>
    public required string RawId { get; init; }

    public required string Name { get; init; }

    /// <summary>The profile's picture, when it has one; the initial shows meanwhile.</summary>
    public Uri? ImageUrl { get; init; }

    public required string Initial { get; init; }

    /// <summary><c>#RRGGBB</c>.</summary>
    public required string ColorHex { get; init; }

    /// <summary>The one signed in right now.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>The household's main account.</summary>
    public bool IsMaster { get; init; }

    /// <summary>A PIN is asked for before switching, right now.</summary>
    public bool RequiresPin { get; init; }

    /// <summary>A PIN is set at all.</summary>
    public bool HasPin { get; init; }

    /// <summary>A member of a linked household, not this one.</summary>
    public bool IsOtherHousehold { get; init; }

    public string? MasterUserId { get; init; }

    /// <summary>On the main account: how many profiles it may have.</summary>
    public int? MaxProfiles { get; init; }

    /// <summary>What the sign-in remembers about the household once switched to.</summary>
    public required StoredHousehold StoredHousehold { get; init; }

    /// <summary>An id as Jellyfin writes it either way: lowercase, without dashes.</summary>
    public static string PlainId(string id) => id.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);

    /// <summary>The name, then "PIN required" and "watching now" where they apply.</summary>
    public string AccessibleDescription
    {
        get
        {
            var parts = new List<string> { Name };
            if (RequiresPin) parts.Add("PIN required");
            if (IsCurrent) parts.Add("watching now");
            return string.Join(", ", parts);
        }
    }
}

public enum ProfileProviderKind
{
    Bonfire,
    PlexHome,
}

public static class ProfileProviderKinds
{
    /// <summary>What the PIN belongs to, in messages.</summary>
    public static string ServiceName(this ProfileProviderKind kind) => kind == ProfileProviderKind.Bonfire ? "Bonfire" : "Plex Home";
}

/// <summary>Lists a household's profiles and switches to one. The Apple app's ProfileProvider.</summary>
public interface IProfileProvider
{
    ProfileProviderKind Kind { get; }

    /// <summary>Makes new profiles, where the service can; null where it can't.</summary>
    IProfileCreation? Creation => null;

    /// <summary>Whether the household asks who's watching when the app opens; null where the service has no such setting.</summary>
    Task<bool?> AskOnStartupAsync(CancellationToken ct = default);

    /// <summary>Every profile that can be switched to, the one signed in included. Throws <see cref="ProfileSwitchKind.Unavailable"/> when the service isn't there.</summary>
    Task<IReadOnlyList<HouseholdProfile>> ProfilesAsync(string currentUserId, CancellationToken ct = default);

    Task VerifyPinAsync(string pin, HouseholdProfile profile, CancellationToken ct = default);

    /// <summary>A fresh sign-in as <paramref name="profile"/>, ready to switch to.</summary>
    Task<StoredSession> SwitchSessionAsync(HouseholdProfile profile, string? pin, CancellationToken ct = default);
}

// ---- Making a profile ----

/// <summary>A picture a new profile may have.</summary>
public sealed record ProfileAvatarChoice(string Id, string Name, Uri Url);

public sealed record ProfileLibraryChoice(string Id, string Name, string? CollectionType);

/// <summary>A rating limit: everything up to this age.</summary>
public sealed record ProfileRatingChoice(int Score, string Label);

/// <summary>What the form offers: the pictures, libraries and ratings the server has, and what the household's rules add.</summary>
public sealed record ProfileCreationOptions
{
    public IReadOnlyList<ProfileAvatarChoice> Avatars { get; init; } = [];
    public IReadOnlyList<ProfileLibraryChoice> Libraries { get; init; } = [];
    public IReadOnlyList<ProfileRatingChoice> Ratings { get; init; } = [];
    public string? RatingCountry { get; init; }

    /// <summary>The main account's own PIN is asked for before making a profile (the account has one; the server may insist on it).</summary>
    public bool AsksMasterPin { get; init; }

    /// <summary>How many more profiles the account may make; null when not known.</summary>
    public int? Remaining { get; init; }
}

/// <summary>A new profile as it's filled in. The Apple app's ProfileDraft.</summary>
public sealed record ProfileDraft
{
    public string Name { get; init; } = "";
    public string Pin { get; init; } = "";

    /// <summary>One of the offered pictures, or null for the initial on the colour.</summary>
    public string? AvatarId { get; init; }

    public string ColorHex { get; init; } = Bonfire.DefaultAvatarColor;

    /// <summary>Everything up to this rating; null for no limit beyond the account's.</summary>
    public int? RatingScore { get; init; }

    /// <summary>The libraries it may see; null for all the account has, now and later.</summary>
    public IReadOnlySet<string>? LibraryIds { get; init; }

    public string MasterPin { get; init; } = "";

    /// <summary>A PIN that isn't asked on the home network.</summary>
    public bool BypassPinOnLocalNetwork { get; init; }

    /// <summary>The colours offered for the initial's tile.</summary>
    public static IReadOnlyList<string> Colors { get; } = ["#00A4DC", "#1F77B4", "#30A46C", "#FFB224", "#F76B15", "#E5484D", "#E93D82", "#8E4EC6"];

    public string TrimmedName => Name.Trim(' ');

    public string Initial => TrimmedName.Length == 0 ? "" : StringInfo.GetNextTextElement(TrimmedName).ToUpper(CultureInfo.CurrentCulture);

    /// <summary>What the server would turn down, said before it's asked (its rules: the name's length and letters, the PIN's digits); null when it's fine.</summary>
    public string? ValidationMessage(string confirmPin)
    {
        var name = TrimmedName;
        if (name.Length == 0) return "Enter a name for the profile.";
        if (new StringInfo(name).LengthInTextElements > Bonfire.MaxProfileNameLength)
            return $"A profile name can be at most {Bonfire.MaxProfileNameLength} characters.";
        if (name.EnumerateRunes().Any(r => !Rune.IsLetterOrDigit(r) && Rune.GetUnicodeCategory(r) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                && !"-'._@+ ".Contains((char)r.Value, StringComparison.Ordinal)))
            return "A profile name can use letters, numbers, spaces and - ' . _ @ +.";
        if (Pin.Length > 0)
        {
            if (Pin.Length < Bonfire.MinPinLength || Pin.Length > Bonfire.MaxPinLength || !Pin.All(char.IsAsciiDigit)) return "A PIN is 4 to 8 digits.";
            if (confirmPin != Pin) return "The PINs don't match.";
        }
        return null;
    }
}

/// <summary>Makes a profile in the household.</summary>
public interface IProfileCreation
{
    /// <summary>The pictures, libraries and ratings to offer. Each list is empty when the server can't say.</summary>
    Task<ProfileCreationOptions> CreationChoicesAsync(CancellationToken ct = default);

    /// <summary>Makes the profile: its plain id.</summary>
    Task<string> CreateProfileAsync(ProfileDraft draft, CancellationToken ct = default);
}

public enum ProfileSwitchKind
{
    WrongPin,
    RateLimited,
    PinNeeded,

    /// <summary>The service isn't on this server.</summary>
    Unavailable,

    Failed,
}

/// <summary>Why a switch (or making a profile) didn't go through, in words to show.</summary>
public sealed class ProfileSwitchException(ProfileSwitchKind kind, string? message = null) : Exception(message ?? Describe(kind))
{
    public ProfileSwitchKind Kind { get; } = kind;

    private static string Describe(ProfileSwitchKind kind) => kind switch
    {
        ProfileSwitchKind.WrongPin => "That PIN isn't right.",
        ProfileSwitchKind.RateLimited => "Too many tries. Wait a few minutes, then try again.",
        ProfileSwitchKind.PinNeeded => "This profile needs its PIN.",
        ProfileSwitchKind.Unavailable => "This server has no profiles.",
        _ => "That didn't work.",
    };
}

/// <summary>
/// How to get to a profile: the current one, a sign-in already saved, a
/// switch the server can do at once, or one that needs the PIN first. A
/// saved sign-in never stands in for a PIN.
/// </summary>
public abstract record ProfileSwitchPlan
{
    public sealed record AlreadyCurrent : ProfileSwitchPlan;
    public sealed record ReuseStored(StoredSession Session) : ProfileSwitchPlan;
    public sealed record SwitchNow : ProfileSwitchPlan;
    public sealed record NeedsPin : ProfileSwitchPlan;

    public static ProfileSwitchPlan Make(HouseholdProfile profile, StoredSession? stored)
    {
        if (profile.IsCurrent) return new AlreadyCurrent();
        if (profile.RequiresPin) return new NeedsPin();
        if (stored is not null) return new ReuseStored(stored);
        return new SwitchNow();
    }
}

/// <summary>When the app asks who's watching as it opens.</summary>
public enum ProfileGateMode
{
    /// <summary>As the household's setting on the server says (Bonfire's "ask on startup"); never for a service without one.</summary>
    Server,

    /// <summary>Every time the app opens.</summary>
    Always,

    Never,
}

public static class ProfileGate
{
    public static string Title(this ProfileGateMode mode) => mode switch
    {
        ProfileGateMode.Always => "Every Time",
        ProfileGateMode.Never => "Never",
        _ => "As the Server Is Set",
    };

    /// <summary>
    /// Whether to ask who's watching on a cold launch, from what's known
    /// without asking the server: the saved sign-in, the setting here, and
    /// the household's preference as last read.
    /// </summary>
    public static bool ShouldAsk(StoredSession stored, ProfileGateMode mode, bool? cachedAskOnStartup)
    {
        var inHousehold = stored.Household is not null || stored.Plex is { HomeUserUuid: not null, HomeToken: not null };
        return mode switch
        {
            ProfileGateMode.Never => false,
            ProfileGateMode.Always => inHousehold,
            _ => stored.Household is not null && cachedAskOnStartup == true,
        };
    }
}

/// <summary>Why the picker is up: the app opened, someone asked to switch, or the profile's PIN is asked again (away, or idle, too long).</summary>
public enum ProfilePickerReason
{
    Launch,
    Switcher,
    Locked,
}

/// <summary>
/// When a profile with a PIN was left, and whether it's locked until its PIN
/// is given: kept in files beside the server's own (AwayClock), so neither
/// is got past by quitting.
/// </summary>
public static class ProfileLockClock
{
    private static string LeftPath => Path.Combine(AppPaths.Data, "profile-left-at");
    private static string LockedPath => Path.Combine(AppPaths.Data, "locked-profile");

    /// <summary>The choices offered, in minutes; 0 is Never.</summary>
    public static IReadOnlyList<int> Choices { get; } = [5, 15, 30, 60, 0];

    /// <summary>The sign-in (<see cref="SavedServer.Id"/>) locked until its profile's PIN is given.</summary>
    public static string? Locked
    {
        get
        {
            try { return File.Exists(LockedPath) ? File.ReadAllText(LockedPath) : null; }
            catch (IOException) { return null; }
        }
        set
        {
            try
            {
                if (value is null) File.Delete(LockedPath);
                else File.WriteAllText(LockedPath, value);
            }
            catch (IOException) { }
        }
    }

    /// <summary>When Aster was left, with a profile that has a PIN.</summary>
    public static DateTimeOffset? LeftAt
    {
        get
        {
            try { return File.Exists(LeftPath) && DateTimeOffset.TryParse(File.ReadAllText(LeftPath), CultureInfo.InvariantCulture, DateTimeStyles.None, out var left) ? left : null; }
            catch (IOException) { return null; }
        }
    }

    /// <summary>The clock starts, unless it's already running.</summary>
    public static void NoteLeft()
    {
        if (LeftAt is not null) return;
        try { File.WriteAllText(LeftPath, DateTimeOffset.UtcNow.ToString("O")); }
        catch (IOException) { }
    }

    /// <summary>Back in time (or unlocked): the clock stops.</summary>
    public static void ClearLeft()
    {
        try { File.Delete(LeftPath); }
        catch (IOException) { }
    }

    /// <summary>Whether coming back at <paramref name="now"/> asks the PIN again: away at least <paramref name="minutes"/> (never with 0).</summary>
    public static bool Overdue(DateTimeOffset? leftAt, DateTimeOffset now, int minutes) =>
        minutes > 0 && leftAt is { } left && now - left >= TimeSpan.FromMinutes(minutes);

    public static string Title(int minutes) => minutes <= 0 ? "Never" : SavedServer.LockTitle(minutes);
}

/// <summary>Each household's "ask on startup" as last read, so the decision on a cold launch needs no network. The Apple app's ProfileGateCache.</summary>
public static class ProfileGateCache
{
    public static bool? AskOnStartup(string householdKey) =>
        AppSettings.Current.ProfileAskOnStartup.TryGetValue(householdKey, out var ask) ? ask : null;

    public static void Set(string householdKey, bool? ask)
    {
        var settings = AppSettings.Current;
        var all = new Dictionary<string, bool>(settings.ProfileAskOnStartup);
        if (ask is { } value) all[householdKey] = value;
        else all.Remove(householdKey);
        if (all.Count == settings.ProfileAskOnStartup.Count && all.All(p => settings.ProfileAskOnStartup.TryGetValue(p.Key, out var old) && old == p.Value)) return;
        AppSettings.Save(settings with { ProfileAskOnStartup = all });
    }
}
