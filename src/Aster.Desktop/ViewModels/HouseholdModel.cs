using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Diagnostics;
using Aster.Core.Jellyfin;
using Aster.Core.Profiles;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The household of the server in use: whether it has profiles to switch
/// between (Bonfire on Jellyfin, a Plex Home), who they are, whether it
/// asks who's watching on opening, and whether the person signed in can add
/// to it. One per sign-in (each shell its own); asked as the shell starts,
/// and again after a while. The Apple app's HouseholdProfiles.
/// </summary>
public sealed partial class HouseholdModel : ObservableObject
{
    private readonly StoredSession session;
    private readonly Action<StoredHousehold> noteHousehold;
    private DateTimeOffset? checkedAt;

    public HouseholdModel(IMediaServer client, StoredSession session, Action<StoredHousehold> noteHousehold)
    {
        this.session = session;
        this.noteHousehold = noteHousehold;
        madeProvider = MakeProvider(client, session);
    }

    /// <summary>The service the server could have, before it's known whether it does.</summary>
    private readonly IProfileProvider? madeProvider;

    /// <summary>Null while there's no service (or not yet known).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(CanCreateProfiles), nameof(IsBonfire), nameof(HasService))]
    public partial IProfileProvider? Provider { get; private set; }

    /// <summary>The server has a household at all (Settings' Who's Watching shows).</summary>
    public bool HasService => Provider is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAvailable), nameof(Current), nameof(Master), nameof(CanCreateProfiles),
        nameof(AvatarInitial), nameof(AvatarBrush), nameof(AvatarImage), nameof(AvatarName))]
    public partial IReadOnlyList<HouseholdProfile> Profiles { get; private set; } = [];

    // ---- Who's watching, small (the sidebar's avatar): the profile, else the user signed in ----

    public string AvatarInitial => Current?.Initial ?? (session.UserName.Length > 0 ? session.UserName[..1].ToUpperInvariant() : "?");
    public Avalonia.Media.IBrush AvatarBrush => ProfileColors.Brush(Current?.ColorHex ?? Aster.Core.Profiles.Bonfire.DefaultAvatarColor);
    public Uri? AvatarImage => Current?.ImageUrl;
    public string AvatarName => Current?.Name ?? session.UserName;

    /// <summary>The household's "ask on startup", as the server last said.</summary>
    public bool? AskOnStartup { get; private set; }

    /// <summary>Whether the server has been asked yet, for this sign-in.</summary>
    public bool Checked { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>Something to open the picker for: someone else to switch to, or room to add someone.</summary>
    public bool IsAvailable => Provider is not null && (Profiles.Count > 1 || CanCreateProfiles);

    public bool IsBonfire => Provider?.Kind == ProfileProviderKind.Bonfire;

    public HouseholdProfile? Current => Profiles.FirstOrDefault(p => p.IsCurrent);

    /// <summary>This household's main account.</summary>
    public HouseholdProfile? Master => Profiles.FirstOrDefault(p => p.IsMaster && !p.IsOtherHousehold);

    /// <summary>The main account, signed in, on a service that makes profiles.</summary>
    public bool CanCreateProfiles => Provider?.Creation is not null && Current is { IsMaster: true, IsOtherHousehold: false };

    private string CacheKey => Snapshots.Key(session.ServerUrl, session.UserId, "household-profiles");

    /// <summary>Asks the service what the household is. A server without one, or Emby, clears it.</summary>
    public async Task RefreshAsync(bool force = false)
    {
        if (!force && Checked && checkedAt is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromMinutes(30)) return;
        if ((Provider ?? madeProvider) is not { } provider)
        {
            Checked = true;
            return;
        }
        try
        {
            var list = await provider.ProfilesAsync(session.UserId);
            Provider = provider;
            Profiles = list;
            LastError = null;
            _ = Snapshots.WriteAsync(CacheKey, list.ToList());
            DebugLog.Note("Profiles", $"{provider.Kind.ServiceName()} present: {list.Count} profiles, {list.Count(p => p.RequiresPin)} with a PIN");
            if (list.FirstOrDefault(p => p.IsCurrent) is { } current)
            {
                noteHousehold(current.StoredHousehold);
                try
                {
                    if (await provider.AskOnStartupAsync() is { } ask)
                    {
                        AskOnStartup = ask;
                        ProfileGateCache.Set(session.ServerUrl.AbsoluteUri + "|" + current.StoredHousehold.MasterUserId, ask);
                        DebugLog.Note("Profiles", $"Ask on startup: {ask}");
                    }
                }
                catch (Exception error) when (IsExpected(error)) { }
            }
        }
        catch (ProfileSwitchException unavailable) when (unavailable.Kind == ProfileSwitchKind.Unavailable)
        {
            Provider = null;
            Profiles = [];
            DebugLog.Note("Profiles", "No profiles on this server");
        }
        catch (Exception error) when (IsExpected(error))
        {
            // Out of reach for a moment: what was known stays.
            LastError = error.Message;
            DebugLog.Note("Profiles", $"Profiles couldn't be read: {error.Message}");
        }
        Checked = true;
        checkedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The list as last read for this sign-in, at once, while the server is asked again. Nothing if it's known already.</summary>
    public async Task RestoreCachedAsync()
    {
        if (Profiles.Count > 0 || madeProvider is null) return;
        if (await Snapshots.ReadAsync<List<HouseholdProfile>>(CacheKey) is not { Count: > 0 } kept || Profiles.Count > 0) return;
        Provider ??= madeProvider;
        Profiles = kept;
    }

    // ---- Making a profile ----

    /// <summary>What the form offers, with the household's own rules: the main account's PIN is asked when it has one, and how many more profiles it may make.</summary>
    public async Task<ProfileCreationOptions> CreationOptionsAsync()
    {
        if (Provider?.Creation is not { } creation) return new ProfileCreationOptions();
        var options = await creation.CreationChoicesAsync();
        options = options with { AsksMasterPin = Master?.HasPin ?? false };
        if (Master?.MaxProfiles is { } limit)
            options = options with { Remaining = Math.Max(0, limit - Profiles.Count(p => !p.IsMaster && !p.IsOtherHousehold)) };
        return options;
    }

    /// <summary>Makes the profile and reads the household again, so it's there to pick: its plain id.</summary>
    public async Task<string> CreateAsync(ProfileDraft draft)
    {
        if (Provider?.Creation is not { } creation) throw new ProfileSwitchException(ProfileSwitchKind.Unavailable);
        var id = await creation.CreateProfileAsync(draft);
        await RefreshAsync(force: true);
        return id;
    }

    private static bool IsExpected(Exception error) =>
        error is ProfileSwitchException or JellyfinException or System.Net.Http.HttpRequestException or TimeoutException or TaskCanceledException;

    /// <summary>The service for the server in use, if it could have one.</summary>
    private static IProfileProvider? MakeProvider(IMediaServer client, StoredSession session) => session.Kind switch
    {
        ServerKind.Jellyfin when client is JellyfinClient jellyfin => new BonfireProfiles(jellyfin, session.ServerName),
        ServerKind.Plex when session.Plex is { } link => PlexHomeProfiles.For(link, session.ServerUrl, session.ServerName),
        _ => null,
    };
}
