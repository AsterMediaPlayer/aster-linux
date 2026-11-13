using Aster.Core.Diagnostics;
using Aster.Core.Jellyfin;
using Aster.Core.Plex;

namespace Aster.Core.Profiles;

/// <summary>
/// A Jellyfin server's household, through the Bonfire plugin: who's in it,
/// switching, and making a new profile. The Apple app's BonfireProfiles.
/// </summary>
public sealed class BonfireProfiles(JellyfinClient jellyfin, string? serverName) : IProfileProvider, IProfileCreation
{
    public BonfireClient Client { get; } = new(jellyfin);
    public ProfileProviderKind Kind => ProfileProviderKind.Bonfire;
    public IProfileCreation? Creation => this;

    /// <summary>The plugin's version and features, once asked (1.9 and later say).</summary>
    public BonfireInfo? Info { get; private set; }

    private bool askedInfo;

    public async Task<bool?> AskOnStartupAsync(CancellationToken ct = default) => (await Client.PreferencesAsync(ct).ConfigureAwait(false)).AskOnStartup;

    public async Task<IReadOnlyList<HouseholdProfile>> ProfilesAsync(string currentUserId, CancellationToken ct = default)
    {
        var records = await Client.ListAsync(ct).ConfigureAwait(false) ?? throw new ProfileSwitchException(ProfileSwitchKind.Unavailable);
        await NoteInfoAsync(ct).ConfigureAwait(false);
        return records.Select(r => FromBonfire(r, Client, currentUserId)).ToList();
    }

    public Task VerifyPinAsync(string pin, HouseholdProfile profile, CancellationToken ct = default) => Client.VerifyPinAsync(profile.RawId, pin, ct);

    public async Task<StoredSession> SwitchSessionAsync(HouseholdProfile profile, string? pin, CancellationToken ct = default)
    {
        if (profile.RequiresPin && string.IsNullOrEmpty(pin)) throw new ProfileSwitchException(ProfileSwitchKind.PinNeeded);
        var result = await Client.SwitchProfileAsync(profile.RawId, pin, ct).ConfigureAwait(false);
        // The plain id: the same user signed in through the sign-in screen
        // (AuthenticateByName writes it that way) lands on the same saved entry, cache and settings.
        return new StoredSession
        {
            ServerUrl = jellyfin.BaseUrl, ServerName = serverName,
            UserId = HouseholdProfile.PlainId(result.JellyfinUserId), UserName = profile.Name,
            AccessToken = result.ActiveProfileToken, ServerKind = Jellyfin.ServerKind.Jellyfin,
            Household = profile.StoredHousehold,
        };
    }

    // ---- Making a profile ----

    /// <summary>All three at once; one that fails leaves its list empty rather than holding the form up (no ratings is still a form).</summary>
    public async Task<ProfileCreationOptions> CreationChoicesAsync(CancellationToken ct = default)
    {
        var avatars = OrNull(Client.AvatarsAsync(ct));
        var libraries = OrNull(Client.LibrariesAsync(ct));
        var ratings = OrNull(Client.RatingsAsync(ct));
        return Options(await avatars.ConfigureAwait(false), await libraries.ConfigureAwait(false), await ratings.ConfigureAwait(false), Client);
    }

    public async Task<string> CreateProfileAsync(ProfileDraft draft, CancellationToken ct = default)
    {
        var created = await Client.CreateProfileAsync(BonfireCreateRequest.From(draft), ct).ConfigureAwait(false);
        DebugLog.Note("Profiles", $"Made profile {created.ProfileName}");
        return HouseholdProfile.PlainId(created.ProfileUserId);
    }

    private static async Task<T?> OrNull<T>(Task<T> task) where T : class
    {
        try { return await task.ConfigureAwait(false); }
        catch (Exception error) when (error is ProfileSwitchException or HttpRequestException or TimeoutException or JellyfinException) { return null; }
    }

    /// <summary>Which Bonfire this is, for the debug report; once per sign-in.</summary>
    private async Task NoteInfoAsync(CancellationToken ct)
    {
        if (askedInfo) return;
        askedInfo = true;
        BonfireInfo? info = null;
        try { info = await Client.InfoAsync(ct).ConfigureAwait(false); }
        catch (Exception error) when (error is ProfileSwitchException or HttpRequestException or TimeoutException or JellyfinException) { }
        if (info is null)
        {
            DebugLog.Note("Profiles", "Bonfire before 1.9 (no info route)");
            return;
        }
        Info = info;
        var features = info.Features;
        DebugLog.Note("Profiles", $"Bonfire {info.Version ?? "?"} (API {info.ApiVersion ?? 0}){(info.Disabled == true ? ", switched off" : "")}"
            + $", app PIN sign-in {(features?.ClientPinLogin == true ? "on" : "off")}, app profile list {(features?.ClientProfileList == true ? "on" : "off")}"
            + (info.Maintenance is { } maintenance ? $", maintenance: {maintenance.Message}" : ""));
    }

    /// <summary>A household entry as the picker shows it.</summary>
    public static HouseholdProfile FromBonfire(BonfireProfileRecord record, BonfireClient client, string currentUserId)
    {
        var plain = HouseholdProfile.PlainId(record.ProfileUserId);
        var name = record.ProfileName;
        var initial = string.IsNullOrEmpty(record.AvatarInitial) ? (name.Length > 0 ? name[..1].ToUpperInvariant() : "") : record.AvatarInitial;
        var color = string.IsNullOrEmpty(record.AvatarColor) ? Bonfire.DefaultAvatarColor : record.AvatarColor;
        var isMaster = record.IsMaster ?? false;
        var masterId = record.MasterUserId is { } master ? HouseholdProfile.PlainId(master) : null;
        Uri? image = null;
        if (!string.IsNullOrEmpty(record.ProfileImage))
        {
            // A picture elsewhere is loaded from there; anything stored on the server comes through the plugin's image route.
            image = record.ProfileImage.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || record.ProfileImage.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? Uri.TryCreate(record.ProfileImage, UriKind.Absolute, out var elsewhere) ? elsewhere : null
                : client.ImageUrl(record.ProfileUserId);
        }
        return new HouseholdProfile
        {
            Id = plain, RawId = record.ProfileUserId, Name = name, ImageUrl = image,
            Initial = initial, ColorHex = color,
            IsCurrent = plain == HouseholdProfile.PlainId(currentUserId),
            IsMaster = isMaster,
            RequiresPin = record.RequiresPin ?? record.HasPin ?? false,
            HasPin = record.HasPin ?? false,
            IsOtherHousehold = record.IsBonfire ?? false,
            MasterUserId = masterId,
            MaxProfiles = record.MaxSubProfiles,
            StoredHousehold = new StoredHousehold
            {
                MasterUserId = masterId ?? plain, IsMaster = isMaster,
                AvatarInitial = initial, AvatarColor = color, ProfileImage = record.ProfileImage,
            },
        };
    }

    /// <summary>The server's lists as the form shows them: pictures by the plugin's small route, ratings youngest first with their labels joined.</summary>
    public static ProfileCreationOptions Options(BonfireAvatarLibrary? avatars, IReadOnlyList<BonfireLibrary>? libraries, BonfireRatings? ratings, BonfireClient client) => new()
    {
        Avatars = (avatars?.Avatars ?? []).Select(a => new ProfileAvatarChoice(a.Id, a.DisplayName ?? a.Id, client.AvatarUrl(a.Id))).ToList(),
        Libraries = (libraries ?? []).Select(l => new ProfileLibraryChoice(l.Id, l.Name, l.CollectionType)).ToList(),
        Ratings = (ratings?.Ratings ?? []).OrderBy(r => r.Score).Select(r => new ProfileRatingChoice(r.Score, string.Join(" / ", r.Labels))).ToList(),
        RatingCountry = ratings?.Country,
    };
}

/// <summary>
/// A Plex Home's people, as a household the picker can switch between.
/// Switching asks plex.tv, with the person's PIN where they have one, for
/// their own token, then the server's token for them. The Apple app's PlexHomeProfiles.
/// </summary>
public sealed class PlexHomeProfiles : IProfileProvider
{
    /// <summary>Plex's gold, for a person without a picture.</summary>
    public const string PlexColor = "#E5A00D";

    private readonly PlexLink link;
    private readonly string homeToken;
    private readonly Uri serverUrl;
    private readonly string? serverName;

    public ProfileProviderKind Kind => ProfileProviderKind.PlexHome;

    private PlexHomeProfiles(PlexLink link, string homeToken, Uri serverUrl, string? serverName)
    {
        this.link = link;
        this.homeToken = homeToken;
        this.serverUrl = serverUrl;
        this.serverName = serverName;
    }

    /// <summary>Null for a sign-in from before the Home's token was kept: it can't check a PIN, so it can't switch.</summary>
    public static PlexHomeProfiles? For(PlexLink link, Uri serverUrl, string? serverName) =>
        link is { HomeToken: { } token, HomeUserUuid: not null } ? new PlexHomeProfiles(link, token, serverUrl, serverName) : null;

    public Task<bool?> AskOnStartupAsync(CancellationToken ct = default) => Task.FromResult<bool?>(null);

    public async Task<IReadOnlyList<HouseholdProfile>> ProfilesAsync(string currentUserId, CancellationToken ct = default)
    {
        var users = await PlexAccount.HomeUsersAsync(homeToken, ct).ConfigureAwait(false);
        if (users.Count == 0) throw new ProfileSwitchException(ProfileSwitchKind.Unavailable);
        return users.Select(u => FromPlex(u, link, currentUserId)).ToList();
    }

    public async Task VerifyPinAsync(string pin, HouseholdProfile profile, CancellationToken ct = default) =>
        _ = await SwitchUserAsync(profile, pin, ct).ConfigureAwait(false);

    public async Task<StoredSession> SwitchSessionAsync(HouseholdProfile profile, string? pin, CancellationToken ct = default)
    {
        var switched = await SwitchUserAsync(profile, pin, ct).ConfigureAwait(false);
        var userToken = switched.AuthToken ?? homeToken;
        // The server's token for this person (a shared server's is its own).
        PlexResource? server = null;
        try { server = (await PlexAccount.ServersAsync(userToken, ct).ConfigureAwait(false)).FirstOrDefault(s => s.ClientIdentifier == link.MachineId); }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException) { }
        var fresh = new PlexLink
        {
            AccountToken = userToken, MachineId = link.MachineId,
            Connections = server?.RankedConnections ?? link.Connections,
            HomeUserUuid = profile.RawId, HomeUserHasPin = profile.HasPin, HomeToken = homeToken,
        };
        return new StoredSession
        {
            ServerUrl = serverUrl, ServerName = serverName,
            UserId = switched.Uuid, UserName = switched.DisplayName,
            AccessToken = server?.AccessToken ?? userToken, ServerKind = Jellyfin.ServerKind.Plex, Plex = fresh,
        };
    }

    /// <summary>plex.tv lets a profile switch to itself with no PIN at all, so an empty one is never sent for a profile that has one.</summary>
    private async Task<PlexAccountUser> SwitchUserAsync(HouseholdProfile profile, string? pin, CancellationToken ct)
    {
        pin = string.IsNullOrEmpty(pin) ? null : pin;
        if (profile.HasPin && pin is null) throw new ProfileSwitchException(ProfileSwitchKind.PinNeeded);
        try
        {
            return await PlexAccount.SwitchUserAsync(new PlexHomeUser { Uuid = profile.RawId, Title = profile.Name }, pin, homeToken, ct).ConfigureAwait(false);
        }
        catch (JellyfinException refused) when (refused.StatusCode is 401 or 403)
        {
            throw new ProfileSwitchException(ProfileSwitchKind.WrongPin);
        }
        catch (JellyfinException refused) when (refused.StatusCode == 429)
        {
            throw new ProfileSwitchException(ProfileSwitchKind.RateLimited);
        }
    }

    public static HouseholdProfile FromPlex(PlexHomeUser user, PlexLink link, string currentUserId)
    {
        var hasPin = user.Protected ?? false;
        var initial = user.Title.Length > 0 ? user.Title[..1].ToUpperInvariant() : "";
        return new HouseholdProfile
        {
            Id = user.Uuid, RawId = user.Uuid, Name = user.Title,
            ImageUrl = string.IsNullOrEmpty(user.Thumb) ? null : Uri.TryCreate(user.Thumb, UriKind.Absolute, out var thumb) ? thumb : null,
            Initial = initial, ColorHex = PlexColor,
            IsCurrent = user.Uuid == currentUserId,
            IsMaster = user.Admin ?? false,
            RequiresPin = hasPin, HasPin = hasPin,
            StoredHousehold = new StoredHousehold
            {
                MasterUserId = link.MachineId, IsMaster = user.Admin ?? false,
                AvatarInitial = initial, AvatarColor = PlexColor, ProfileImage = user.Thumb,
            },
        };
    }
}
