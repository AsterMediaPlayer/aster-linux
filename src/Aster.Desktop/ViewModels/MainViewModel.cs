using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Jellyfin;
using Aster.Core.Playback;
using Aster.Core.Plex;
using Aster.Core.Profiles;
using Aster.Core.Server;
using Aster.Desktop.Images;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>What the window shows: sign-in, the app (sidebar and pages), or the player over it all.</summary>
public partial class MainViewModel : ViewModelBase, IServerAccounts
{
    [ObservableProperty]
    public partial ViewModelBase? CurrentPage { get; set; }

    private ShellViewModel? shell;


    /// <summary>The last run's crash has been offered this run (it stays on disk until sent or put aside).</summary>

    private bool crashOffered;

    /// <summary>Every server signed in to, to switch between (the development session isn't one).</summary>
    public IReadOnlyList<SavedServer> Servers { get; private set; } = [];

    public string? CurrentId { get; private set; }

    /// <summary>The disc menu open, if one is, even while a title it played is showing.</summary>
    private DiscMenuViewModel? openMenu;

    /// <summary>Signed in from the ASTER_* variables: signing out leaves the saved session alone.</summary>
    private bool developmentSession;

    /// <summary>
    /// The server locked after Lock After, until its password is given (or
    /// it's signed in to afresh): switching back to it asks for it too.
    /// </summary>
    private static string? LockedId
    {
        get => AwayClock.LockedServer;
        set => AwayClock.LockedServer = value;
    }

    /// <param name="playItemId">A title to play straight away (<c>--play &lt;id&gt;</c>), for development.</param>
    /// <param name="maxBitrate">A quality cap for that title (<c>--max-bitrate</c>), to try converted streams.</param>
    /// <param name="startSeconds">Where to start that title (<c>--start</c>), instead of its resume point.</param>
    /// <param name="openItemId">A title's page to open straight away (<c>--open &lt;id&gt;</c>), for development.</param>
    /// <param name="tab">A sidebar tab to start on (<c>--tab movies</c>), for development.</param>
    /// <param name="sort">That tab's sort (<c>--sort DateAdded</c>), for development.</param>
    /// <param name="section">That tab's section (<c>--section guide</c> on Live TV), for development.</param>
    public MainViewModel(string? playItemId = null, int? maxBitrate = null, double? startSeconds = null, string? openItemId = null, AppTab? tab = null,
        Aster.Core.Browsing.LibrarySort? sort = null, string? section = null)
    {
        var development = DevelopmentSession();
        developmentSession = development is not null;
        var stored = StoredSession.Load();
        Servers = SavedServers.LoadOrStart(stored);
        if ((development ?? stored) is { } session)
        {
            var client = MediaServers.For(session);
            ShowShell(client, session);
            if (tab is { } startTab) shell!.ShowTab(startTab);
            if (sort is { } startSort && shell!.CurrentPage is BrowsePageViewModel page) page.SortIndex = (int)startSort;
            if (section is not null && shell!.CurrentPage is LiveTVViewModel live) live.ShowSection(section);
            // Closed while away, or while locked: it's still locked (or its Plex address is looked into).
            CheckLock();
            // Who's watching, before anything of anyone's is used: decided from what's kept here, so
            // opening never waits on the server. After the lock; before the welcome screen.
            if (development is null && CurrentPage is ShellViewModel or WelcomeViewModel)
            {
                if (Aster.Core.Profiles.ProfileGate.ShouldAsk(session, AppSettings.Current.ProfileGate,
                        session.HouseholdKey is { } key ? Aster.Core.Profiles.ProfileGateCache.AskOnStartup(key) : null))
                    ShowProfilePicker(Aster.Core.Profiles.ProfilePickerReason.Launch);
                // Not asking who's watching: a profile with a PIN still asks it, as Aster opens (one without goes straight in).
                else if (session.Household is not null || session.Plex is { HomeUserHasPin: true, HomeToken: not null })
                    LockProfile();
            }
            // A title to open or play as Aster opens (the TV home screen's Continue Watching): once the PIN's given.
            if (openItemId is not null) RunWhenUnlocked(() => _ = OpenByIdAsync(client, openItemId, section));
            if (playItemId is not null) RunWhenUnlocked(() => _ = PlayByIdAsync(client, playItemId, maxBitrate, startSeconds));
        }
        else
        {
            ShowSignIn();
        }
        // A profile with a PIN asks it again after a while with nothing pressed.
        idleCheck.Tick += (_, _) => CheckIdle();
        idleCheck.Start();
    }

    /// <summary>
    /// In a debug build, ASTER_SERVER, ASTER_TOKEN and ASTER_USER_ID sign in
    /// without touching the saved session, for trying the app against a
    /// server with an API key; ASTER_SERVER_KIND=emby or plex for an Emby or
    /// Plex server (with a Plex token, and any user id).
    /// </summary>
    private static StoredSession? DevelopmentSession()
    {
#if DEBUG
        var server = Environment.GetEnvironmentVariable("ASTER_SERVER");
        var token = Environment.GetEnvironmentVariable("ASTER_TOKEN");
        var user = Environment.GetEnvironmentVariable("ASTER_USER_ID");
        if (server is not null && token is not null && user is not null)
        {
            var kind = Enum.TryParse<ServerKind>(Environment.GetEnvironmentVariable("ASTER_SERVER_KIND"), ignoreCase: true, out var named) ? named : ServerKind.Jellyfin;
            return new StoredSession { ServerUrl = new Uri(server), UserId = user, UserName = "Development", AccessToken = token, ServerKind = kind };
        }
#endif
        return null;
    }

    /// <param name="serverAddress">Filled in, to sign in to the same server again.</param>
    private void ShowSignIn(string? message = null, string? serverAddress = null)
    {
        shell?.Dispose();
        shell = null;
        ImageLoader.Client = null;
        CurrentId = null;
        CurrentPage = new SignInViewModel(SignedIn, Servers, SwitchTo, ShowPlexSignIn) { Message = message, ServerAddress = serverAddress ?? "" };
    }

    /// <summary>Plex's sign-in, through plex.tv; Cancel comes back to the sign-in screen.</summary>
    public void ShowPlexSignIn() =>
        CurrentPage = new PlexSignInViewModel(session =>
        {
            session.Save();
            SignedIn(MediaServers.For(session), session);
        }, () => ShowSignIn());

    /// <summary>Plex's sign-in from a saved Plex server's page, to pick its Home profile afresh; Cancel comes back to the page.</summary>
    public void SignInWithPlexAgain()
    {
        var back = CurrentPage;
        CurrentPage = new PlexSignInViewModel(session =>
        {
            session.Save();
            developmentSession = false;
            SignedIn(MediaServers.For(session), session);
        }, () => CurrentPage = back);
    }

    /// <summary>A sign-in that worked: kept with the others, and used.</summary>
    private void SignedIn(IMediaServer client, StoredSession session)
    {
        // Signing in afresh is as good as the password.
        if (LockedId == SavedServer.IdOf(session)) Unlocked();
        Servers = SavedServers.Remember(Servers, session);
        SavedServers.Save(Servers);
        ShowShell(client, session);
    }

    private void ShowShell(IMediaServer client, StoredSession session)
    {
        // Another server's: what the last one was showing and playing goes.
        shell?.Dispose();
        ImageLoader.Client = client;
        CurrentId = developmentSession ? null : SavedServer.IdOf(session);
        shell = new ShellViewModel(client, session, (entry, closed) => Play(client, entry, closed),
            // Disc menus come from Aster's Jellyfin plugin.
            (document, itemId, closed) => ShowDiscMenu((JellyfinClient)client, document, itemId, closed), kind => SignOutAsync(client, kind), this);
        CurrentPage = shell;
        // The last run ended in a crash: offered once, to send with a report.
        if (!crashOffered && Aster.Core.Diagnostics.DebugLog.LastCrash() is not null)
        {
            crashOffered = true;
            // After the first tab is chosen, which would clear it away.
            var offering = shell;
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (shell == offering) offering.OpenDebugReport(afterCrash: true);
            }, Avalonia.Threading.DispatcherPriority.Background);
        }
        // Once for each Jellyfin server, people signed in before it included.
        if (CurrentId is { } id && !AppSettings.Current.WelcomeSeen.Contains(id)) ShowWelcome();
    }

    /// <summary>
    /// What Aster and its companion plugins do, over the app, for a Jellyfin
    /// server (the plugins are Jellyfin's); Start Watching marks it seen and
    /// comes back. The Apple app's needsWelcome.
    /// </summary>
    public void ShowWelcome()
    {
        if (shell is not { Jellyfin: { IsJellyfin: true } client } || CurrentId is not { } id) return;
        var app = shell;
        CurrentPage = new WelcomeViewModel(client, app.ServerName, () =>
        {
            var settings = AppSettings.Current;
            if (!settings.WelcomeSeen.Contains(id)) AppSettings.Save(settings with { WelcomeSeen = [.. settings.WelcomeSeen, id] });
            // Signed out, switched or locked meanwhile: leave it.
            if (shell == app) CurrentPage = app;
        });
    }

    // ---- Household profiles ----

    public void ShowProfilePicker(Aster.Core.Profiles.ProfilePickerReason reason)
    {
        if (shell is not { } app) return;
        if (reason == Aster.Core.Profiles.ProfilePickerReason.Switcher && !app.Household.IsAvailable) return;
        CurrentPage = new ProfilePickerViewModel(reason, app.Household, this, app.UserName, app.ServerName, () =>
        {
            // Switched meanwhile: the new sign-in's own screen is up already.
            if (shell != app) return;
            CurrentPage = app;
            // The welcome screen, once, after who's watching was asked as the app opened.
            if (reason == Aster.Core.Profiles.ProfilePickerReason.Launch && CurrentId is { } id && !AppSettings.Current.WelcomeSeen.Contains(id)) ShowWelcome();
            RunAfterUnlock();
        }, page => CurrentPage = page);
    }

    public StoredSession? ReusableSession(string userId)
    {
        if (shell is null || StoredSession.Load() is not { } current) return null;
        var id = current.Plex is { } plex ? $"plex:{plex.MachineId}|{userId}" : current.ServerUrl.AbsoluteUri + "|" + userId;
        return Servers.FirstOrDefault(s => s.Id == id)?.Session;
    }

    public void SwitchProfile(StoredSession fresh)
    {
        Aster.Core.Diagnostics.DebugLog.Note("Profiles", $"Switching to {fresh.UserName}");
        // Whoever was locked is left behind: the profile switched to starts afresh, without what waited on the PIN.
        afterUnlock = null;
        ProfileLockClock.Locked = null;
        ProfileLockClock.ClearLeft();
        lastInput = DateTimeOffset.UtcNow;
        // The household's welcome (what Aster's plugins do) was seen by whoever signed in; a profile isn't shown it again.
        var id = SavedServer.IdOf(fresh);
        var settings = AppSettings.Current;
        if (!settings.WelcomeSeen.Contains(id)) AppSettings.Save(settings with { WelcomeSeen = [.. settings.WelcomeSeen, id] });
        Use(fresh);
    }

    public void NoteHousehold(StoredHousehold household)
    {
        if (developmentSession || StoredSession.Load() is not { } stored || SavedServer.IdOf(stored) != CurrentId || stored.Household == household) return;
        stored = stored with { Household = household };
        stored.Save();
        Servers = SavedServers.Remember(Servers, stored);
        SavedServers.Save(Servers);
    }

    /// <summary>Uses a saved server, asking for its password first when it's set to, or locked.</summary>
    public void SwitchTo(SavedServer server)
    {
        if (server.Id == CurrentId) return;
        if (!(server.AsksPasswordToSwitch && server.CanBeLocked) && server.Id != LockedId)
        {
            Use(server.Session);
            return;
        }
        var back = CurrentPage;
        CurrentPage = new ServerPasswordViewModel(server, locking: false, fresh =>
        {
            if (server.Id == LockedId) Unlocked();
            Use(fresh);
        }, () => CurrentPage = back);
    }

    public void ConfirmPassword(SavedServer server, string purpose, string action, Action confirmed)
    {
        var back = CurrentPage;
        CurrentPage = new ServerPasswordViewModel(server, locking: false, _ =>
        {
            CurrentPage = back;
            confirmed();
        }, () => CurrentPage = back, purpose, action);
    }

    public void Change(string id, Func<SavedServer, SavedServer> change)
    {
        Servers = SavedServers.Change(Servers, id, change);
        SavedServers.Save(Servers);
    }

    /// <summary>A saved sign-in, or a fresh one from its password: the app starts afresh on it.</summary>
    private void Use(StoredSession session)
    {
        session.Save();
        developmentSession = false;
        Servers = SavedServers.Remember(Servers, session);
        SavedServers.Save(Servers);
        ShowShell(MediaServers.For(session), session);
    }

    public void Remove(string id)
    {
        if (id == CurrentId || Servers.FirstOrDefault(s => s.Id == id) is not { } server) return;
        Servers = SavedServers.Forget(Servers, id);
        SavedServers.Save(Servers);
        var session = server.Session;
        _ = MediaServers.For(session).LogoutAsync()
            .ContinueWith(_ => { }, TaskScheduler.Default);
    }

    // ---- Lock After ----

    private SavedServer? CurrentServer => Servers.FirstOrDefault(s => s.Id == CurrentId);

    /// <summary>
    /// The window went to the background (or is closing): the clock for the
    /// server's Lock After starts. Not while a video or disc menu is up, as
    /// one plays on while another window has the keyboard; closing always.
    /// The welcome screen counts as the app.
    /// </summary>
    public void NoteLeft(bool closing = false)
    {
        if ((closing ? shell is not null : CurrentPage is ShellViewModel or WelcomeViewModel) && CurrentServer?.LockAfterMinutes is not null) AwayClock.NoteLeft();
        // The profile's clock too, with a PIN to ask (a video's page counts: it paused as the window went).
        if ((closing ? shell is not null : CurrentPage is ShellViewModel or WelcomeViewModel or PlayerViewModel) && ProfileAsksPin && AppSettings.Current.ProfileLockMinutes > 0)
            ProfileLockClock.NoteLeft();
    }

    // ---- Waiting on the PIN ----

    /// <summary>What was asked for while the PIN was: done once it's given; dropped for another profile.</summary>
    private Action? afterUnlock;

    /// <summary>
    /// Does <paramref name="action"/> (a title handed over to play, say) now,
    /// or, while "Who's watching?" waits on a PIN (as Aster opens, or locked),
    /// once it's given; never past it.
    /// </summary>
    public void RunWhenUnlocked(Action action)
    {
        if (CurrentPage is ProfilePickerViewModel { Reason: ProfilePickerReason.Launch or ProfilePickerReason.Locked })
        {
            afterUnlock = action;
            return;
        }
        action();
    }

    /// <summary>The picker let the one signed in carry on: what waited on it, now.</summary>
    private void RunAfterUnlock()
    {
        var waiting = afterUnlock;
        afterUnlock = null;
        waiting?.Invoke();
    }

    // ---- The profile's PIN, again after a while ----

    private readonly Avalonia.Threading.DispatcherTimer idleCheck = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTimeOffset lastInput = DateTimeOffset.UtcNow;

    /// <summary>A key, a click, the pointer moving, a controller's press: not idle.</summary>
    public void NoteInput() => lastInput = DateTimeOffset.UtcNow;

    /// <summary>The profile signed in asks a PIN to switch to it (now: not one skipping it on the home network).</summary>
    private bool ProfileAsksPin => shell?.Household.Current is { RequiresPin: true }
        || (shell?.Household.Current is null && CurrentServer?.Session.Plex is { HomeUserHasPin: true });

    /// <summary>
    /// Nothing pressed for the setting's minutes, with a profile that has a
    /// PIN: it's asked again. Not while a video plays (paused, it counts),
    /// nor over a sign-in, a server's lock or the picker itself.
    /// </summary>
    private void CheckIdle()
    {
        var minutes = AppSettings.Current.ProfileLockMinutes;
        if (minutes <= 0 || shell is null || !ProfileAsksPin) return;
        if (CurrentPage is not (ShellViewModel or WelcomeViewModel or PlayerViewModel or ProfileCreateViewModel)) return;
        if (Aster.Desktop.Audio.VideoMedia.Current?.NowPlaying is { IsPlaying: true })
        {
            lastInput = DateTimeOffset.UtcNow;
            return;
        }
        if (DateTimeOffset.UtcNow - lastInput >= TimeSpan.FromMinutes(minutes)) LockProfile();
    }

    /// <summary>
    /// The profile's PIN before carrying on: "Who's watching?" over the app,
    /// on the profile's PIN, with no way past it but the PIN (or another
    /// profile). A video pauses under it; the page comes back as it was.
    /// </summary>
    private void LockProfile()
    {
        if (shell is not { } app || CurrentId is not { } id || CurrentPage is ProfilePickerViewModel { IsLocked: true }) return;
        ProfileLockClock.Locked = id;
        ProfileLockClock.ClearLeft();
        Aster.Desktop.Audio.VideoMedia.Current?.Run(Aster.Desktop.Audio.MediaCommand.Pause);
        Aster.Core.Diagnostics.DebugLog.Note("Profiles", "Locked: the PIN is asked again");
        var back = CurrentPage is ProfilePickerViewModel or ProfileCreateViewModel ? app : CurrentPage;
        CurrentPage = new ProfilePickerViewModel(Aster.Core.Profiles.ProfilePickerReason.Locked, app.Household, this, app.UserName, app.ServerName, () =>
        {
            ProfileLockClock.Locked = null;
            ProfileLockClock.ClearLeft();
            lastInput = DateTimeOffset.UtcNow;
            if (shell != app) return;
            CurrentPage = back ?? app;
            RunAfterUnlock();
        }, page => CurrentPage = page);
    }

    /// <summary>The window is back (or Aster just opened): locks the server in use if it was away past its Lock After.</summary>
    public void CheckLock()
    {
        if (shell is null || CurrentServer is not { } server) return;
        if (server.Id == LockedId || server.LocksAfter(AwayClock.LeftAt, DateTimeOffset.UtcNow))
        {
            Lock(server);
            return;
        }
        AwayClock.Clear();
        // The profile's own PIN, after a while away (or still locked when Aster closed).
        if (ProfileLockClock.Locked == CurrentId
            || ProfileLockClock.Overdue(ProfileLockClock.LeftAt, DateTimeOffset.UtcNow, AppSettings.Current.ProfileLockMinutes))
            LockProfile();
        else ProfileLockClock.ClearLeft();
        _ = FindPlexAgainAsync();
    }

    // ---- Plex's address ----

    private bool findingPlex;

    /// <summary>
    /// On opening and on coming back: when a Plex server's saved address has
    /// stopped answering (this computer on another network, the server's
    /// address changed), another that answers takes over, and the app starts
    /// afresh on it. The Apple app's refreshPlexConnection.
    /// </summary>
    private async Task FindPlexAgainAsync()
    {
        if (findingPlex || shell is null || CurrentServer?.Session is not { Plex: not null } stored) return;
        findingPlex = true;
        try
        {
            if (await PlexReconnect.FindAgainAsync(stored) is not { } found) return;
            // Signed out, switched or locked meanwhile: leave it.
            if (shell is null || CurrentId != SavedServer.IdOf(found)) return;
            System.Diagnostics.Debug.WriteLine($"Plex: now reached at {found.ServerUrl}");
            Use(found);
        }
        finally
        {
            findingPlex = false;
        }
    }

    /// <summary>Asks for the password to carry on: what was showing and playing goes.</summary>
    private void Lock(SavedServer server)
    {
        LockedId = server.Id;
        AwayClock.Clear();
        shell?.Dispose();
        shell = null;
        ImageLoader.Client = null;
        CurrentPage = new ServerPasswordViewModel(server, locking: true, fresh =>
        {
            Unlocked();
            Use(fresh);
        }, () => ShowSignIn());
    }

    private void Unlocked()
    {
        LockedId = null;
        AwayClock.Clear();
    }

    /// <summary>
    /// Plays over everything; closing comes back to the page it was played
    /// from. Played from the app's pages it's a layer over them, so it can
    /// shrink to the mini player and play on while they're browsed.
    /// </summary>
    private void Play(IMediaServer server, PlaybackQueueItem entry, Action<BaseItem?>? closed)
    {
        var over = shell is { } open && CurrentPage == open ? open : null;
        var player = new PlayerViewModel(server, entry, last =>
        {
            if (over is not null) over.Video = null;
            else CurrentPage = shell;
            closed?.Invoke(last);
        })
        { CanMinimize = over is not null };
        if (OpensChannelRow) player.OpenFlickWhenStarted();
        if (StartsPaused) player.PauseWhenStarted();
        if (over is not null) over.Video = player;
        else CurrentPage = player;
    }

    /// <summary>
    /// A disc menu over everything. What it plays plays over it, and closing
    /// the player comes back to the menu; closing the menu comes back to the
    /// page it was opened from.
    /// </summary>
    private void ShowDiscMenu(JellyfinClient client, Aster.Core.DiscMenu.DiscMenuDocument document, string itemId, Action closed)
    {
        DiscMenuViewModel? menu = null;
        openMenu = menu = new DiscMenuViewModel(client, document, itemId,
            play: entry => CurrentPage = new PlayerViewModel(client, entry, played =>
            {
                CurrentPage = menu;
                _ = menu!.ResumeAfterPlayback();
            }),
            close: () =>
            {
                CurrentPage = shell;
                openMenu = null;
                menu!.Dispose();
                closed();
            });
        CurrentPage = menu;
    }

    /// <summary>Opens a page for <c>--open</c>: looked up first, so a library or person opens as one would from a card.</summary>
    /// <summary>For screenshots (<c>--disc-menu</c>): the <c>--open</c> title's disc menu opens once it's known.</summary>
    public bool OpensDiscMenu { get; set; }

    /// <summary>For trying the panel (<c>--play-all</c>, <c>--play-all-full</c>): the <c>--open</c> show's Play All once its Music tab is ready; 2, full screen.</summary>
    public int PlaysAll { get; set; }

    /// <summary>For trying it (<c>--mini</c>, with <c>--tab livetv</c>): that channel tuned straight into the Live TV header.</summary>
    public async void TuneMiniPlayer(string channelId)
    {
        if (shell?.CurrentPage is not LiveTVViewModel live) return;
        try { await live.Mini.StartAsync(await live.Client.ItemAsync(channelId)); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>
    /// For trying it (<c>--edit-channels</c>, with a playlist id for one
    /// channel alone): Edit Channels; with <c>--new-channel</c>, a channel
    /// made from that show as if it were picked.
    /// </summary>
    /// <summary>For trying it (<c>--trailer &lt;YouTube id&gt;</c>): that trailer's page.</summary>
    public void OpenTrailer(string youTubeId)
    {
        if (shell is { } open) open.Push(new TrailerViewModel(open, youTubeId, "Trailer"));
    }

    public void OpenEditChannels(string? only, string? newChannel = null)
    {
        if (shell is not { } open || open.Client is not Aster.Core.Channels.IChannelServer) return;
        var page = new EditChannelsViewModel(open, only);
        open.Push(page);
        if (newChannel is not null) _ = page.MakeChannelAsync(newChannel);
    }

    /// <summary>For trying the pause screen (<c>--paused</c>): what <c>--play</c> plays pauses once it starts.</summary>
    public bool StartsPaused { get; set; }

    /// <summary>For trying it (<c>--playlist-picker</c>): the <c>--open</c> title's Add to Playlist.</summary>
    public bool OpensPlaylistPicker { get; set; }

    /// <summary>For screenshots (<c>--now-playing</c>): music played with <c>--play</c> shows Now Playing.</summary>
    public bool OpensNowPlaying { get; set; }

    /// <summary>For screenshots (<c>--flick</c>): a channel's row shows once it plays.</summary>
    public bool OpensChannelRow { get; set; }

    /// <summary>For screenshots (<c>--channel 2</c>): tunes in the Channel with that number once the Channels tab has them.</summary>
    public async void TuneChannelWhenLoaded(int number)
    {
        if (shell?.CurrentPage is not ChannelsViewModel channels) return;
        await channels.Loaded;
        if (Aster.Core.Channels.ChannelTuner.OrderedChannels.FirstOrDefault(c => c.Number == number) is { } channel) channels.PlayChannel(channel.Id);
    }

    /// <summary>
    /// An <c>aster://</c> link, opened at launch or handed on by a later one:
    /// a title's page, the title playing, or a Channels-tab channel tuned in.
    /// Signed out, it's dropped.
    /// </summary>
    public async void Follow(Aster.Core.DeepLink link)
    {
        if (shell is not { } open) return;
        System.Diagnostics.Trace.WriteLine($"Links: {link.Kind}");
        switch (link.Kind)
        {
            case Aster.Core.DeepLinkKind.Item:
                await OpenByIdAsync(open.Client, link.Id, null);
                break;
            case Aster.Core.DeepLinkKind.Play:
                await PlayByIdAsync(open.Client, link.Id, null, null);
                break;
            case Aster.Core.DeepLinkKind.Channel:
                if (open.Client is not Aster.Core.Channels.IChannelServer) return;
                open.ShowTab(AppTab.Channels);
                if (open.CurrentPage is not ChannelsViewModel channels) return;
                await channels.Loaded;
                channels.PlayChannel(link.Id);
                break;
        }
    }

    /// <param name="section">A tab of the title's page to show once it has something in it (<c>--section music</c>).</param>
    private async Task OpenByIdAsync(IMediaServer client, string itemId, string? section)
    {
        try
        {
            shell?.Open(await client.ItemAsync(itemId));
            if (section is not null && shell?.CurrentPage is DetailViewModel page) page.ShowSectionWhenReady(section);
            if (OpensDiscMenu && shell?.CurrentPage is DetailViewModel titled) titled.OpenDiscMenuWhenReady();
            if (PlaysAll > 0 && shell?.CurrentPage is DetailViewModel show) show.PlayAllWhenReady(fullScreen: PlaysAll == 2);
            if (OpensPlaylistPicker && shell?.CurrentPage is DetailViewModel adding) adding.AddToPlaylistCommand.Execute(null);
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>
    /// Plays a title from where it was left (or <paramref name="startSeconds"/>),
    /// for <c>--play</c>: a song or album in the music player, a book in the
    /// book player, anything else in the video player.
    /// </summary>
    private async Task PlayByIdAsync(IMediaServer client, string itemId, int? maxBitrate, double? startSeconds)
    {
        try
        {
            var item = await client.ItemAsync(itemId);
            if (shell is { } open && client is JellyfinClient music && item.Kind is ItemKind.Audio or ItemKind.MusicAlbum)
            {
                var tracks = item.Kind == ItemKind.Audio ? [item] : await music.AlbumTracksAsync(item.Id);
                open.Audio.Music.Play(tracks, 0, shuffled: false, music);
                if (OpensNowPlaying) open.Audio.IsNowPlayingShown = true;
                return;
            }
            if (shell is { } books && client is JellyfinClient library && item.Kind == ItemKind.AudioBook)
            {
                books.Audio.Books.Play(Aster.Core.Audio.Audiobook.FromItem(item), null, library);
                return;
            }
            var start = startSeconds is { } seconds ? (long)(seconds * 10_000_000) : item.UserData?.PlaybackPositionTicks ?? 0;
            shell?.Play(new PlaybackQueueItem { ItemId = item.Id, StartTicks = start, MaxBitrate = maxBitrate });
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException) { }
    }

    /// <summary>
    /// Off to sign-in. Adding a server keeps the one in use saved and signed
    /// in, to switch back to; signing out (or a session the server ended)
    /// forgets it, and the others stay saved.
    /// </summary>
    private async Task SignOutAsync(IMediaServer client, SignOutKind kind)
    {
        var address = kind == SignOutKind.AddServer ? null : client.BaseUrl.ToString().TrimEnd('/');
        if (developmentSession)
        {
            // Neither ends the key's access nor forgets the real sign-in.
            developmentSession = false;
            ShowSignIn("Signed out of the development session.", address);
            return;
        }
        if (kind != SignOutKind.AddServer && CurrentId is { } current)
        {
            Servers = SavedServers.Forget(Servers, current);
            SavedServers.Save(Servers);
        }
        if (kind == SignOutKind.SignOut)
        {
            try { await client.LogoutAsync(); }
            catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
        }
        StoredSession.Clear();
        // Last time's screens, kept to show at once, go with the sign-in.
        if (kind != SignOutKind.AddServer) Aster.Core.Snapshots.Clear();
        ShowSignIn(kind == SignOutKind.Expired ? "Your session has ended. Please sign in again." : null, address);
    }

    /// <summary>The app is closing: a title playing reports where it stopped.</summary>
    public void Shutdown()
    {
        NoteLeft(closing: true);
        if ((CurrentPage as PlayerViewModel ?? shell?.Video) is { } player)
        {
            player.SaveProgressNow();
            player.Stop();
            player.FinishReporting(TimeSpan.FromSeconds(2));
        }
        // A disc menu, whether showing or waiting behind a title it's playing.
        openMenu?.Dispose();
        shell?.Dispose();
    }
}
