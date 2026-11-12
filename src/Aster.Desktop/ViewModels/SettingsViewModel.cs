using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A choice in a settings list, shown by its title.</summary>
public sealed record SettingOption<T>(string Title, T Value)
{
    public override string ToString() => Title;
}

/// <summary>A saved server in Settings: its name, Jellyfin or Emby and who's signed in, and its page.</summary>
/// <summary>A library switched on or off for somewhere, by name.</summary>
public sealed record LibrarySwitch(string Name, bool IsOn, IRelayCommand Flip);

public sealed record ServerRow(string Name, string Detail, bool IsCurrent, bool AsksPassword, IRelayCommand Open)
{
    /// <summary>Another server that asks for its password to switch: a lock beside it.</summary>
    public bool ShowsLock => AsksPassword && !IsCurrent;
}

/// <summary>A page of Settings, opened from its list.</summary>
/// <summary>Settings' pages, as the Apple app's SettingsCategory (Audio &amp; Subtitles and Disc Menus open from Video).</summary>
public enum SettingsPage
{
    Streaming,
    Video,
    AudioSubtitles,
    DiscMenus,
    Music,
    Anime,
    TitlePages,
    General,
    Account,
    About,
}

/// <summary>
/// The Settings tab, as the Apple app's: its pages under headings (Playback:
/// Streaming &amp; Data and Video; Library: Anime, Title Pages and Channels;
/// This Device: General; Services: Server Plugins; then Account and About),
/// each opened over it. This holds what they all show and change. The Up Next, intro,
/// theme, disc menu and styled-subtitle switches follow the user between
/// devices (<see cref="SettingsSync"/>); the languages are the user's own
/// Jellyfin settings, shared with the web app.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly INavigator navigator;
    private readonly Func<SignOutKind, Task> signOut;
    private UserConfiguration? configuration;

    /// <summary>True while values are being shown, rather than chosen.</summary>
    private bool showing;

    public SettingsViewModel(INavigator navigator, string userName, string serverName, Func<SignOutKind, Task> signOut, IServerAccounts accounts)
    {
        this.accounts = accounts;
        ShowServers();
        this.navigator = navigator;
        this.signOut = signOut;
        UserName = userName;
        ServerName = serverName;
        ServerAddress = navigator.Client.BaseUrl.ToString().TrimEnd('/');
        ShowLocal();
        _ = LoadAsync();
        _ = ShowAnimeMusicVideosAsync();
        _ = FindTrackingAsync();
        // Bonfire or a Plex Home turning out to be there changes Ask on Opening's choices and words.
        navigator.Household.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(HouseholdModel.Provider)) return;
            OnPropertyChanged(nameof(ProfileGateChoices));
            OnPropertyChanged(nameof(ProfileGateTitle));
            OnPropertyChanged(nameof(ProfileGateNote));
        };
    }

    /// <summary>
    /// Music Videos in Anime: each music video library but the OP/ED plugin's
    /// (which the Anime tab always has), to give it a shelf there too. Only
    /// with anime and such a library, as the Apple app's.
    /// </summary>
    [ObservableProperty] public partial IReadOnlyList<LibrarySwitch> AnimeMusicVideoSwitches { get; set; } = [];

    public bool HasAnimeMusicVideoSwitches => AnimeMusicVideoSwitches.Count > 0;

    partial void OnAnimeMusicVideoSwitchesChanged(IReadOnlyList<LibrarySwitch> value) => OnPropertyChanged(nameof(HasAnimeMusicVideoSwitches));

    private async Task ShowAnimeMusicVideosAsync()
    {
        var catalog = await navigator.Anime;
        if (!catalog.HasAnime) return;
        var chosen = AppSettings.Current.AnimeMusicVideoLibraries;
        AnimeMusicVideoSwitches = catalog.OtherMusicVideoLibraries.Select(l => l.Name).OfType<string>().Distinct()
            .Select(name => new LibrarySwitch(name, chosen.Contains(name), new RelayCommand(() => FlipAnimeMusicVideos(name)))).ToList();
    }

    private void FlipAnimeMusicVideos(string name)
    {
        Change(s => s with
        {
            AnimeMusicVideoLibraries = s.AnimeMusicVideoLibraries.Contains(name)
                ? s.AnimeMusicVideoLibraries.Where(n => n != name).ToList()
                : [.. s.AnimeMusicVideoLibraries, name],
        });
        _ = ShowAnimeMusicVideosAsync();
    }

    /// <summary>Opens one of the pages over the list.</summary>
    [RelayCommand] private void Open(SettingsPage page) => navigator.Push(new SettingsPageViewModel(this, page));

    /// <summary>Aster's companion plugins on the server: the welcome screen's switches, any time.</summary>
    [RelayCommand] private void OpenServerPlugins() => navigator.Push(new ServerPluginsPageViewModel(navigator.JellyfinOnly));

    /// <summary>Services ▸ Tracking: only with the server's Aster Discovery and Tracking plugin, which links the services.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServices))]
    public partial Aster.Core.Tracking.TrackingFeatures? TrackingFeatures { get; set; }

    public bool HasTracking => TrackingFeatures is { Services.Count: > 0 };
    public bool HasServices => HasTracking || IsJellyfin;

    partial void OnTrackingFeaturesChanged(Aster.Core.Tracking.TrackingFeatures? value) => OnPropertyChanged(nameof(HasTracking));

    private async Task FindTrackingAsync() => TrackingFeatures = await navigator.Tracking;

    [RelayCommand]
    private void OpenTracking()
    {
        if (TrackingFeatures is { } features && navigator.Jellyfin is { } jellyfin) navigator.Push(new TrackingPageViewModel(navigator, jellyfin, features));
    }

    // ---- Account: household profiles ----

    /// <summary>The household of the server in use, for Switch Profile and Who's Watching.</summary>
    public HouseholdModel Household => navigator.Household;

    /// <summary>Who's watching, over the app.</summary>
    [RelayCommand] private void SwitchProfile() => accounts.ShowProfilePicker(Aster.Core.Profiles.ProfilePickerReason.Switcher);

    /// <summary>Ask on Opening's choices: As the Server Is Set only where the server has a setting to follow (Bonfire's; a Plex Home hasn't).</summary>
    public IReadOnlyList<TrackChoice> ProfileGateChoices => Choices(
        Enum.GetValues<Aster.Core.Profiles.ProfileGateMode>()
            .Where(mode => mode != Aster.Core.Profiles.ProfileGateMode.Server || Household.IsBonfire)
            .Select(mode => new SettingOption<Aster.Core.Profiles.ProfileGateMode>(Aster.Core.Profiles.ProfileGate.Title(mode), mode)).ToList(),
        ProfileGateOption, option => ProfileGate = option.Value);

    /// <summary>The setting, as shown: a Plex Home set to follow the server shows Never, which is what that means there.</summary>
    private SettingOption<Aster.Core.Profiles.ProfileGateMode> ProfileGateOption
    {
        get
        {
            var mode = ProfileGate == Aster.Core.Profiles.ProfileGateMode.Server && !Household.IsBonfire ? Aster.Core.Profiles.ProfileGateMode.Never : ProfileGate;
            return new(Aster.Core.Profiles.ProfileGate.Title(mode), mode);
        }
    }

    public string ProfileGateTitle => ProfileGateOption.Title;

    public string ProfileGateNote => Household.IsBonfire
        ? "Whether Aster asks who's watching as it opens. As the Server Is Set follows the household's Ask on startup setting in Bonfire, as Jellyfin's own apps do."
        : "Whether Aster asks who's watching as it opens, like Plex's own apps.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProfileGateChoices), nameof(ProfileGateTitle))]
    public partial Aster.Core.Profiles.ProfileGateMode ProfileGate { get; set; }

    partial void OnProfileGateChanged(Aster.Core.Profiles.ProfileGateMode value) => Change(s => s with { ProfileGate = value });

    /// <summary>General's Show Welcome Screen: the welcome screen again, with Aster's server plugins to switch on.</summary>
    [RelayCommand] private void ShowWelcome() => accounts.ShowWelcome();

    /// <summary>Edit Channels, where an administrator makes the first channel (the Channels tab shows only once there's one).</summary>
    [RelayCommand] private void OpenChannels() => navigator.Push(new EditChannelsViewModel(navigator));



    /// <summary>Turns a switch over, by its property's name: a row's Enter or click.</summary>
    [RelayCommand]
    private void Flip(string setting)
    {
        switch (setting)
        {
            case nameof(FullScreenPlayback): FullScreenPlayback = !FullScreenPlayback; break;
            case nameof(AutoplayNext): AutoplayNext = !AutoplayNext; break;
            case nameof(AutoSkipIntro): AutoSkipIntro = !AutoSkipIntro; break;
            case nameof(StyledSubtitles): StyledSubtitles = !StyledSubtitles; break;
            case nameof(ThemeCards): ThemeCards = !ThemeCards; break;
            case nameof(ThemeMusic): ThemeMusic = !ThemeMusic; break;
            case nameof(ThemeVideos): ThemeVideos = !ThemeVideos; break;
            case nameof(OpeningSound): OpeningSound = !OpeningSound; break;
            case nameof(SongLookups): SongLookups = !SongLookups; break;
            case nameof(SwapControllerButtons): SwapControllerButtons = !SwapControllerButtons; break;
            case nameof(GaplessMusic): GaplessMusic = !GaplessMusic; break;
            case nameof(SaveUpcomingSongs): SaveUpcomingSongs = !SaveUpcomingSongs; break;
            case nameof(ThemePanel): ThemePanel = !ThemePanel; break;
            case nameof(AudioPassthrough): AudioPassthrough = !AudioPassthrough; break;
            case nameof(EvenVolume): EvenVolume = !EvenVolume; break;
            case nameof(StereoMix): StereoMix = !StereoMix; break;
            case nameof(PreferDiscMenu): PreferDiscMenu = !PreferDiscMenu; break;
            case nameof(DiscMenuSounds): DiscMenuSounds = !DiscMenuSounds; break;
        }
    }

    /// <summary>A list's choices for its menu, the one chosen ticked.</summary>
    private static IReadOnlyList<TrackChoice> Choices<T>(IReadOnlyList<SettingOption<T>> options, SettingOption<T>? current, Action<SettingOption<T>> pick) =>
        options.Select((option, i) => new TrackChoice(i, option.Title, option == current, new RelayCommand(() => pick(option)))).ToList();

    // ---- Playback ----

    public IReadOnlyList<SettingOption<int>> Bitrates { get; } =
    [
        new("Auto (120 Mbps)", 120_000_000),
        new("60 Mbps", 60_000_000),
        new("40 Mbps", 40_000_000),
        new("20 Mbps", 20_000_000),
        new("10 Mbps", 10_000_000),
        new("4 Mbps", 4_000_000),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BitrateChoices))]
    public partial SettingOption<int>? Bitrate { get; set; }

    public IReadOnlyList<TrackChoice> BitrateChoices => Choices(Bitrates, Bitrate, b => Bitrate = b);

    /// <summary>Music's own quality, as the Apple app lists it: Original plays songs as they are, FLAC included.</summary>
    public IReadOnlyList<SettingOption<int>> MusicBitrates { get; } =
    [
        new("Original (no conversion)", 0), new("320 kbps", 320_000), new("256 kbps", 256_000), new("192 kbps", 192_000), new("128 kbps", 128_000),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MusicBitrateChoices))]
    public partial SettingOption<int>? MusicBitrate { get; set; }

    public IReadOnlyList<TrackChoice> MusicBitrateChoices => Choices(MusicBitrates, MusicBitrate, b => MusicBitrate = b);

    partial void OnMusicBitrateChanged(SettingOption<int>? value)
    {
        if (value is not null) Change(s => s with { MusicMaxBitrate = value.Value });
    }

    [ObservableProperty] public partial bool GaplessMusic { get; set; }
    [ObservableProperty] public partial bool SaveUpcomingSongs { get; set; }
    partial void OnGaplessMusicChanged(bool value) => Change(s => s with { GaplessMusic = value });
    partial void OnSaveUpcomingSongsChanged(bool value) => Change(s => s with { SaveUpcomingSongs = value });

    public IReadOnlyList<SettingOption<VideoBack>> VideoBacks { get; } =
    [
        new("Ask each time", Aster.Core.VideoBack.Ask),
        new("Mini player", Aster.Core.VideoBack.MiniPlayer),
        new("Stop", Aster.Core.VideoBack.Stop),
    ];

    /// <summary>What Back does in a video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VideoBackChoices))]
    public partial SettingOption<VideoBack>? VideoBack { get; set; }

    public IReadOnlyList<TrackChoice> VideoBackChoices => Choices(VideoBacks, VideoBack, b => VideoBack = b);

    partial void OnVideoBackChanged(SettingOption<VideoBack>? value)
    {
        if (value is not null) Change(s => s with { BackInVideo = value.Value });
    }
    [ObservableProperty] public partial bool FullScreenPlayback { get; set; }
    [ObservableProperty] public partial bool AutoplayNext { get; set; }
    [ObservableProperty] public partial bool AutoSkipIntro { get; set; }
    [ObservableProperty] public partial bool StyledSubtitles { get; set; }
    [ObservableProperty] public partial bool ThemeCards { get; set; }
    [ObservableProperty] public partial bool ThemeMusic { get; set; }
    [ObservableProperty] public partial bool ThemeVideos { get; set; }
    [ObservableProperty] public partial bool OpeningSound { get; set; }
    [ObservableProperty] public partial bool SongLookups { get; set; }
    [ObservableProperty] public partial bool SwapControllerButtons { get; set; }

    /// <summary>Title Pages ▸ Ratings: the viewer's own MDBList key, kept with the sign-ins; saved as it's typed, as the Apple app's.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RatingsNote))]
    public partial string MdbListKey { get; set; } = "";

    partial void OnMdbListKeyChanged(string value)
    {
        if (!showing) Aster.Core.Ratings.MdbList.ApiKey = value;
    }

    public string RatingsNote => string.IsNullOrWhiteSpace(MdbListKey)
        ? "Title pages show the scores your server has. With a free MDBList key (from mdblist.com, under Preferences › API Access), films and shows also get IMDb, Rotten Tomatoes (critics and audience), Metacritic, Letterboxd and TMDB scores. The key stays on this computer, with your sign-ins."
        : "Films and shows get IMDb, Rotten Tomatoes, Metacritic, Letterboxd and TMDB scores from MDBList. Clear the key to stop asking it.";

    /// <summary>Refresh songs' word on what it did ("Done"), beside it.</summary>
    [ObservableProperty] public partial string? SongsRefreshed { get; set; }

    /// <summary>Every song name found forgotten, so each show's are looked up again as its page opens.</summary>
    [RelayCommand]
    private void RefreshSongs()
    {
        Aster.Core.Anime.AnimeThemesLookup.Forget();
        SongsRefreshed = "Done";
    }
    [ObservableProperty] public partial bool ThemePanel { get; set; }
    [ObservableProperty] public partial bool AudioPassthrough { get; set; }
    [ObservableProperty] public partial bool EvenVolume { get; set; }
    [ObservableProperty] public partial bool StereoMix { get; set; }

    public IReadOnlyList<SettingOption<ThemeCut>> ThemeCuts { get; } =
        Enum.GetValues<ThemeCut>().Select(cut => new SettingOption<ThemeCut>(cut.SettingTitle, cut)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThemeCutChoices))]
    public partial SettingOption<ThemeCut>? ThemeCut { get; set; }

    public IReadOnlyList<TrackChoice> ThemeCutChoices => Choices(ThemeCuts, ThemeCut, c => ThemeCut = c);

    partial void OnThemeCutChanged(SettingOption<ThemeCut>? value)
    {
        if (value is not null) Change(s => s with { ThemeCut = value.Value });
    }
    public IReadOnlyList<SettingOption<SubtitleSize>> SubtitleSizes { get; } =
        Enum.GetValues<SubtitleSize>().Select(size => new SettingOption<SubtitleSize>(size.Title(), size)).ToList();
    public IReadOnlyList<SettingOption<SubtitleColor>> SubtitleColors { get; } =
        Enum.GetValues<SubtitleColor>().Select(color => new SettingOption<SubtitleColor>(color.Title(), color)).ToList();
    public IReadOnlyList<SettingOption<SubtitleBackground>> SubtitleBackgrounds { get; } =
        Enum.GetValues<SubtitleBackground>().Select(back => new SettingOption<SubtitleBackground>(back.Title(), back)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleSizeChoices))]
    public partial SettingOption<SubtitleSize>? SubtitleSize { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleColorChoices))]
    public partial SettingOption<SubtitleColor>? SubtitleColor { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleBackgroundChoices))]
    public partial SettingOption<SubtitleBackground>? SubtitleBackground { get; set; }

    public IReadOnlyList<TrackChoice> SubtitleSizeChoices => Choices(SubtitleSizes, SubtitleSize, c => SubtitleSize = c);
    public IReadOnlyList<TrackChoice> SubtitleColorChoices => Choices(SubtitleColors, SubtitleColor, c => SubtitleColor = c);
    public IReadOnlyList<TrackChoice> SubtitleBackgroundChoices => Choices(SubtitleBackgrounds, SubtitleBackground, c => SubtitleBackground = c);

    partial void OnSubtitleSizeChanged(SettingOption<SubtitleSize>? value)
    {
        if (value is not null) Change(s => s with { SubtitleSize = value.Value });
    }

    partial void OnSubtitleColorChanged(SettingOption<SubtitleColor>? value)
    {
        if (value is not null) Change(s => s with { SubtitleColor = value.Value });
    }

    partial void OnSubtitleBackgroundChanged(SettingOption<SubtitleBackground>? value)
    {
        if (value is not null) Change(s => s with { SubtitleBackground = value.Value });
    }

    [ObservableProperty] public partial bool PreferDiscMenu { get; set; }
    [ObservableProperty] public partial bool DiscMenuSounds { get; set; }

    partial void OnBitrateChanged(SettingOption<int>? value)
    {
        if (value is not null) Change(s => s with { MaxBitrate = value.Value });
    }

    partial void OnFullScreenPlaybackChanged(bool value) => Change(s => s with { FullScreenPlayback = value });
    partial void OnAutoplayNextChanged(bool value) => Change(s => s with { AutoplayNext = value });
    partial void OnAutoSkipIntroChanged(bool value) => Change(s => s with { AutoSkipIntro = value });
    partial void OnStyledSubtitlesChanged(bool value) => Change(s => s with { StyledSubtitles = value });
    partial void OnThemeCardsChanged(bool value) => Change(s => s with { ThemeCards = value });
    partial void OnThemeMusicChanged(bool value) => Change(s => s with { ThemeMusic = value });
    partial void OnThemeVideosChanged(bool value) => Change(s => s with { ThemeVideos = value });
    partial void OnOpeningSoundChanged(bool value) => Change(s => s with { OpeningSound = value });
    partial void OnSongLookupsChanged(bool value) => Change(s => s with { SongLookups = value });
    partial void OnSwapControllerButtonsChanged(bool value) => Change(s => s with { SwapControllerButtons = value });
    partial void OnThemePanelChanged(bool value) => Change(s => s with { ThemePanel = value });
    partial void OnAudioPassthroughChanged(bool value) => Change(s => s with { AudioPassthrough = value });
    partial void OnEvenVolumeChanged(bool value) => Change(s => s with { EvenVolume = value });
    partial void OnStereoMixChanged(bool value) => Change(s => s with { StereoMix = value });
    partial void OnPreferDiscMenuChanged(bool value) => Change(s => s with { PreferDiscMenu = value });
    partial void OnDiscMenuSoundsChanged(bool value) => Change(s => s with { DiscMenuSounds = value });

    private void Change(Func<AppSettings, AppSettings> change)
    {
        if (showing) return;
        // Following the user only where the server keeps settings; otherwise just here.
        if (navigator.Jellyfin is { } jellyfin && jellyfin.Can(ServerCapabilities.SettingsSync)) _ = SettingsSync.ChangeAsync(jellyfin, change);
        else AppSettings.Save(change(AppSettings.Current));
    }

    /// <summary>This computer's settings, as they are now.</summary>
    private void ShowLocal()
    {
        var settings = AppSettings.Current;
        showing = true;
        Bitrate = Bitrates.FirstOrDefault(b => b.Value == settings.MaxBitrate) ?? Bitrates[0];
        MusicBitrate = MusicBitrates.FirstOrDefault(b => b.Value == settings.MusicMaxBitrate) ?? MusicBitrates[0];
        GaplessMusic = settings.GaplessMusic;
        SaveUpcomingSongs = settings.SaveUpcomingSongs;
        VideoBack = VideoBacks.FirstOrDefault(b => b.Value == settings.BackInVideo) ?? VideoBacks[0];
        FullScreenPlayback = settings.FullScreenPlayback;
        AutoplayNext = settings.AutoplayNext;
        AutoSkipIntro = settings.AutoSkipIntro;
        StyledSubtitles = settings.StyledSubtitles;
        ThemeCards = settings.ThemeCards;
        ThemeMusic = settings.ThemeMusic;
        ThemeVideos = settings.ThemeVideos;
        OpeningSound = settings.OpeningSound;
        SongLookups = settings.SongLookups;
        SwapControllerButtons = settings.SwapControllerButtons;
        ProfileGate = settings.ProfileGate;
        MdbListKey = Aster.Core.Ratings.MdbList.ApiKey ?? "";
        ThemePanel = settings.ThemePanel;
        AudioPassthrough = settings.AudioPassthrough;
        EvenVolume = settings.EvenVolume;
        StereoMix = settings.StereoMix;
        ThemeCut = ThemeCuts.FirstOrDefault(c => c.Value == settings.ThemeCut) ?? ThemeCuts[0];
        SubtitleSize = SubtitleSizes.First(o => o.Value == settings.SubtitleSize);
        SubtitleColor = SubtitleColors.First(o => o.Value == settings.SubtitleColor);
        SubtitleBackground = SubtitleBackgrounds.First(o => o.Value == settings.SubtitleBackground);
        PreferDiscMenu = settings.PreferDiscMenu;
        DiscMenuSounds = settings.DiscMenuSounds;
        SonosSpeaker = settings.SonosSpeaker;
        showing = false;
        _ = FindSonosAsync();
    }

    // ---- The Sonos this computer's TV plays through ----

    /// <summary>The rooms on the network, found as Settings opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SonosChoices), nameof(SonosName))]
    public partial IReadOnlyList<Aster.Core.Audio.SonosRoom> SonosRooms { get; private set; } = [];

    /// <summary>The chosen one's id (RINCON_…), or null for none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SonosChoices), nameof(SonosName))]
    public partial string? SonosSpeaker { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SonosName))]
    public partial bool IsFindingSonos { get; private set; }

    public string SonosName => SonosSpeaker is null ? "None"
        : SonosRooms.FirstOrDefault(r => r.Id == SonosSpeaker)?.Name ?? (IsFindingSonos ? "Finding…" : "Not found");

    public IReadOnlyList<TrackChoice> SonosChoices =>
    [
        new(0, "None", SonosSpeaker is null, new RelayCommand(() => PickSonos(null))),
        .. SonosRooms.Select((room, i) => new TrackChoice(i + 1, room.Name, room.Id == SonosSpeaker, new RelayCommand(() => PickSonos(room.Id)))),
    ];

    /// <summary>Chosen here: this computer's own (not synced), as it's this computer's TV.</summary>
    private void PickSonos(string? id)
    {
        SonosSpeaker = id;
        AppSettings.Save(AppSettings.Current with { SonosSpeaker = id });
    }

    private async System.Threading.Tasks.Task FindSonosAsync()
    {
        IsFindingSonos = true;
        SonosRooms = await Aster.Core.Audio.Sonos.RoomsAsync();
        IsFindingSonos = false;
    }

    // ---- Audio & Subtitles ----

    /// <summary>ISO 639-2 codes as Jellyfin stores them, with an "Any" entry.</summary>
    private static readonly SettingOption<string>[] Languages =
    [
        new("Any", ""), new("English", "eng"), new("Spanish", "spa"), new("French", "fre"), new("German", "ger"),
        new("Italian", "ita"), new("Portuguese", "por"), new("Japanese", "jpn"), new("Korean", "kor"), new("Chinese", "chi"),
        new("Russian", "rus"), new("Arabic", "ara"), new("Hindi", "hin"), new("Dutch", "dut"), new("Swedish", "swe"),
        new("Norwegian", "nor"), new("Danish", "dan"), new("Finnish", "fin"), new("Polish", "pol"), new("Turkish", "tur"),
        new("Hebrew", "heb"), new("Thai", "tha"), new("Vietnamese", "vie"), new("Greek", "gre"), new("Czech", "cze"), new("Hungarian", "hun"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioLanguageChoices))]
    public partial IReadOnlyList<SettingOption<string>> AudioLanguages { get; set; } = Languages;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleLanguageChoices))]
    public partial IReadOnlyList<SettingOption<string>> SubtitleLanguages { get; set; } = Languages;

    public IReadOnlyList<SettingOption<SubtitleMode>> SubtitleModes { get; } =
        Enum.GetValues<SubtitleMode>().Select(m => new SettingOption<SubtitleMode>(m.Title, m)).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioLanguageChoices))]
    public partial SettingOption<string>? AudioLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleLanguageChoices))]
    public partial SettingOption<string>? SubtitleLanguage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubtitleModeChoices))]
    public partial SettingOption<SubtitleMode>? SubtitleModeChoice { get; set; }

    public IReadOnlyList<TrackChoice> AudioLanguageChoices => Choices(AudioLanguages, AudioLanguage, l => AudioLanguage = l);
    public IReadOnlyList<TrackChoice> SubtitleLanguageChoices => Choices(SubtitleLanguages, SubtitleLanguage, l => SubtitleLanguage = l);
    public IReadOnlyList<TrackChoice> SubtitleModeChoices => Choices(SubtitleModes, SubtitleModeChoice, m => SubtitleModeChoice = m);

    /// <summary>The language settings can be changed once the user's server settings are in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageNote))]
    public partial bool HasConfiguration { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LanguageNote))]
    public partial string? ConfigurationError { get; set; }

    /// <summary>The languages are the Jellyfin (or Emby) user's own settings; Plex keeps its own.</summary>
    public bool KeepsLanguages => navigator.Jellyfin is not null;

    public string LanguageNote => !KeepsLanguages
        ? "On Plex, the preferred audio and subtitle languages are your Plex profile's own: choose them in Plex's own settings for your account."
        : ConfigurationError is { } error
        ? $"Your Jellyfin settings couldn't be loaded: {error}"
        : HasConfiguration
            ? "Synced with your Jellyfin profile, so the web app and this PC make the same choices. A title's own Audio & Subtitles menu overrides them for that title."
            : "Loading your Jellyfin settings…";

    partial void OnAudioLanguageChanged(SettingOption<string>? value)
    {
        if (value is not null) UpdateConfiguration(c => c with { AudioLanguagePreference = value.Value });
    }

    partial void OnSubtitleLanguageChanged(SettingOption<string>? value)
    {
        if (value is not null) UpdateConfiguration(c => c with { SubtitleLanguagePreference = value.Value });
    }

    partial void OnSubtitleModeChoiceChanged(SettingOption<SubtitleMode>? value)
    {
        if (value is not null) UpdateConfiguration(c => c with { SubtitleMode = value.Value.ToString() });
    }

    /// <summary>The language list, plus the current value if the server uses a code that isn't in it, so the list never shows nothing.</summary>
    private static IReadOnlyList<SettingOption<string>> LanguagesWith(string current) =>
        current.Length == 0 || Languages.Any(l => l.Value == current) ? Languages : [.. Languages, new(current.ToUpperInvariant(), current)];

    private async void UpdateConfiguration(Func<UserConfiguration, UserConfiguration> change)
    {
        // Only loaded from a Jellyfin or Emby user.
        if (showing || configuration is null || navigator.Jellyfin is not { } jellyfin) return;
        var updated = change(configuration);
        configuration = updated;
        try { await jellyfin.UpdateConfigurationAsync(updated); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            ConfigurationError = error.Message;
        }
    }

    // ---- Account ----

    /// <summary>Disc menus come from a Jellyfin plugin: their page hides on Emby.</summary>
    public bool IsJellyfin => navigator.Jellyfin?.IsJellyfin == true;

    /// <summary>Edit Channels: channels are playlist tags on Jellyfin and Emby, and in a playlist's description on Plex.</summary>
    public bool OffersChannels => navigator.Client is Aster.Core.Channels.IChannelServer;

    /// <summary>"Jellyfin version" or "Emby version".</summary>
    public string ServerVersionLabel => $"{navigator.Client.Kind} version";

    public string UserName { get; }
    public string ServerName { get; }
    public string ServerAddress { get; }
    [ObservableProperty] public partial string? ServerVersion { get; set; }

    // ---- Servers ----

    private readonly IServerAccounts accounts;

    /// <summary>Every server signed in to, each opening its page: to switch to, lock or forget.</summary>
    [ObservableProperty] public partial IReadOnlyList<ServerRow> Servers { get; private set; } = [];

    private void ShowServers() =>
        Servers = accounts.Servers.Select(server => new ServerRow(
            server.Name,
            $"{server.Session.Kind} · {server.Session.UserName}",
            server.Id == accounts.CurrentId,
            server.AsksPasswordToSwitch,
            new RelayCommand(() => navigator.Push(new SavedServerViewModel(navigator, accounts, server, ShowServers))))).ToList();

    /// <summary>Another server, or another user on this one: the one in use stays signed in, to switch back to.</summary>
    [RelayCommand] private Task AddServer() => signOut(SignOutKind.AddServer);

    /// <summary>Signs out of the server in use, which is forgotten; the others stay.</summary>
    [RelayCommand] private Task SignOut() => signOut(SignOutKind.SignOut);

    // ---- About ----

    /// <summary>Send Debug Report: what happened, and the app's log with names and sign-ins taken out.</summary>
    [RelayCommand] private void SendDebugReport() => (navigator as ShellViewModel)?.OpenDebugReport();

    /// <summary>Help &amp; Support or the Privacy Policy, in the browser; its address is shown beside it, for where none opens (Steam's gaming mode).</summary>
    [RelayCommand]
    private void OpenWeb(string url)
    {
        WebMessage = null;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            WebMessage = $"Couldn't open a browser. The page is at {url.Replace("https://", "", StringComparison.Ordinal)}.";
        }
    }

    [ObservableProperty] public partial string? WebMessage { get; set; }

    /// <summary>Open Source &amp; Licenses: Aster's license, and every open-source component in it with its license in full.</summary>
    [RelayCommand] private void OpenSource() => navigator.Push(new OpenSourceViewModel(navigator));

    public string AppVersion { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    /// <summary>
    /// The server's copies: the synced switches, the languages and the
    /// server's version, each shown as it arrives.
    /// </summary>
    private async Task LoadAsync()
    {
        // Settings, languages and the version as Jellyfin and Emby keep them.
        if (navigator.Jellyfin is not { } jellyfin)
        {
            ShowLocal();
            return;
        }
        var pull = jellyfin.Can(ServerCapabilities.SettingsSync) ? SettingsSync.PullAsync(jellyfin) : Task.CompletedTask;
        var info = jellyfin.PublicSystemInfoAsync();
        try
        {
            configuration = (await jellyfin.MeAsync()).Configuration ?? new UserConfiguration();
            var audio = configuration.AudioLanguagePreference ?? "";
            var subtitles = configuration.SubtitleLanguagePreference ?? "";
            var mode = Enum.TryParse<SubtitleMode>(configuration.SubtitleMode, out var parsed) ? parsed : SubtitleMode.Default;
            showing = true;
            AudioLanguages = LanguagesWith(audio);
            SubtitleLanguages = LanguagesWith(subtitles);
            AudioLanguage = AudioLanguages.First(l => l.Value == audio);
            SubtitleLanguage = SubtitleLanguages.First(l => l.Value == subtitles);
            SubtitleModeChoice = SubtitleModes.First(m => m.Value == mode);
            showing = false;
            HasConfiguration = true;
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            ConfigurationError = error.Message;
        }
        await pull;
        ShowLocal();
        try { ServerVersion = (await info).Version; }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
    }
}

/// <summary>One page of Settings, over the list: what it shows is the list's (<see cref="SettingsViewModel"/>).</summary>
public sealed class SettingsPageViewModel(SettingsViewModel settings, SettingsPage page) : ViewModelBase
{
    public SettingsViewModel Settings { get; } = settings;

    public string Title => page switch
    {
        SettingsPage.Streaming => "Streaming & Data",
        SettingsPage.Video => "Video",
        SettingsPage.AudioSubtitles => "Audio & Subtitles",
        SettingsPage.DiscMenus => "Disc Menus",
        SettingsPage.Music => "Music",
        SettingsPage.Anime => "Anime",
        SettingsPage.TitlePages => "Title Pages",
        SettingsPage.General => "General",
        SettingsPage.Account => "Account",
        _ => "About",
    };

    public bool IsStreaming => page == SettingsPage.Streaming;
    public bool IsVideo => page == SettingsPage.Video;
    public bool IsAnime => page == SettingsPage.Anime;
    public bool IsMusic => page == SettingsPage.Music;
    public bool IsGeneral => page == SettingsPage.General;
    public bool IsAudioSubtitles => page == SettingsPage.AudioSubtitles;
    public bool IsDiscMenus => page == SettingsPage.DiscMenus;
    public bool IsTitlePages => page == SettingsPage.TitlePages;
    public bool IsAccount => page == SettingsPage.Account;
    public bool IsAbout => page == SettingsPage.About;
}
