using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.LiveTV;
using Aster.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>A programme that's on: its picture, channel, title and how far through it is.</summary>
public sealed partial class ProgramCard(LiveTVViewModel live, BaseItem program) : ICard
{
    public string Key => program.Id;
    public Uri? Image { get; } = live.Client.ProgramImageUrl(program);
    public string? ChannelLine { get; } = program.ChannelName is null ? null : string.Join(" ", new[] { program.ChannelNumber, program.ChannelName }.OfType<string>());
    public bool IsLive => program.IsLive == true;
    public double? Progress { get; } = program.AiredFraction * 100;
    public bool HasProgress => Progress is not null;
    public string Title => program.DisplayTitle;
    public string Subtitle { get; } = program.EpisodeTitle ?? LiveTVFormat.TimeRange(program.Start, program.End);

    [RelayCommand] private void Open() => live.OpenProgram(program);
}

/// <summary>A channel: its logo (or number), what's on, and Play; favourites from its menu.</summary>
public sealed partial class ChannelCard(LiveTVViewModel live, BaseItem channel) : ObservableObject, ICard
{
    public string Key => channel.Id;
    public Uri? Logo { get; } = live.Client.ChannelLogoUrl(channel);
    public string Badge => channel.ChannelNumber ?? channel.DisplayTitle;
    public string Title { get; } = string.Join("  ", new[] { channel.ChannelNumber, channel.DisplayTitle }.OfType<string>());
    public string? NowTitle => channel.CurrentProgram?.Name;
    public double? Progress { get; } = channel.CurrentProgram?.AiredFraction * 100;
    public bool HasProgress => Progress is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; set; } = channel.UserData?.IsFavorite == true;

    public string FavoriteLabel => IsFavorite ? "Remove from Favorites" : "Add to Favorites";

    [RelayCommand] private void Play() => live.PlayChannel(channel.Id);

    [RelayCommand]
    private void ToggleFavorite()
    {
        IsFavorite = !IsFavorite;
        live.SetFavorite(channel.Id, IsFavorite);
    }
}

/// <summary>A scheduled or in-progress recording of one showing.</summary>
public sealed partial class TimerCard(LiveTVViewModel live, TimerInfo timer) : ICard
{
    public string Key => timer.Id;
    public Uri? Image { get; } = timer.ProgramInfo is { } program ? live.Client.ProgramImageUrl(program) : null;
    public bool IsRecording => timer.Status == "InProgress";
    public string Title => timer.Name ?? "Recording";
    public string Subtitle { get; } = string.Join(" · ", new[]
    {
        timer.ChannelName,
        JellyfinDate.Parse(timer.StartDate) is { } start ? LiveTVFormat.Airing(start, JellyfinDate.Parse(timer.EndDate), DateTimeOffset.Now) : null,
    }.OfType<string>());

    [RelayCommand]
    private void Open()
    {
        if (timer.ProgramInfo is { } program) live.OpenProgram(program);
    }
}

/// <summary>A series recording rule: its picture, name and the gist of its options.</summary>
public sealed partial class SeriesRuleCard(LiveTVViewModel live, SeriesTimerInfo rule) : ICard
{
    public string Key => rule.Id;
    public Uri? Image { get; } = RuleImage(live.Client, rule);
    public string Title => rule.Name ?? "Series Recording";

    public string Subtitle { get; } = string.Join(" · ", new[]
    {
        rule.RecordAnyChannel == true ? null : rule.ChannelName,
        rule.RecordNewOnly == true ? "New episodes" : "All episodes",
        rule.KeepUpTo is > 0 and var keep ? $"Keep {keep}" : null,
    }.OfType<string>());

    private static Uri? RuleImage(JellyfinClient client, SeriesTimerInfo rule)
    {
        if (rule.ImageTags?.GetValueOrDefault("Primary") is { } tag && rule.ProgramId is { } program)
            return client.ImageUrl(program, ImageKind.Primary, tag, maxWidth: 800);
        if (rule.ParentPrimaryImageItemId is { } parent && rule.ParentPrimaryImageTag is { } parentTag)
            return client.ImageUrl(parent, ImageKind.Primary, parentTag, maxWidth: 800);
        if (rule.ChannelId is { } channel && rule.ChannelPrimaryImageTag is { } logo)
            return client.ImageUrl(channel, ImageKind.Primary, logo, maxWidth: 800);
        return null;
    }

    [RelayCommand] private void Open() => live.OpenSeriesRule(rule.Id);
}

/// <summary>
/// The Live TV tab: On Now, the Guide, every channel and recordings, under
/// one header. What they show is loaded here once and shared; each section
/// is its own page beneath it. A port of the Apple app's LiveTVView.
/// </summary>
public sealed partial class LiveTVViewModel : ViewModelBase, IRefreshable
{
    private enum Section { OnNow, Guide, Channels, Recordings }

    private static readonly (Section Section, string Title)[] SectionTitles =
        [(Section.OnNow, "On Now"), (Section.Guide, "Guide"), (Section.Channels, "Channels"), (Section.Recordings, "Recordings")];

    private readonly INavigator navigator;
    private readonly Dictionary<Section, ViewModelBase> pages = [];
    private Section section = Section.OnNow;

    public LiveTVViewModel(INavigator navigator)
    {
        this.navigator = navigator;
        Loaded = LoadAsync();
        RecordingsLoaded = LoadRecordingsAsync();
        Show(Section.OnNow);
    }

    public JellyfinClient Client => navigator.Client;

    // ---- What the sections share ----

    /// <summary>Channels and what's on, loading or loaded.</summary>
    public Task Loaded { get; private set; }

    /// <summary>Recordings, scheduled ones and series rules, loading or loaded.</summary>
    public Task RecordingsLoaded { get; private set; }

    /// <summary>In the guide's order: favourites, then by number.</summary>
    public IReadOnlyList<BaseItem> Channels { get; private set; } = [];

    public IReadOnlyList<BaseItem> OnNow { get; private set; } = [];
    public IReadOnlyList<BaseItem> Movies { get; private set; } = [];
    public IReadOnlyList<BaseItem> Sports { get; private set; } = [];
    public IReadOnlyList<BaseItem> Kids { get; private set; } = [];
    public IReadOnlyList<BaseItem> News { get; private set; } = [];
    public IReadOnlyList<BaseItem> Recordings { get; private set; } = [];
    public IReadOnlyList<TimerInfo> Timers { get; private set; } = [];
    public IReadOnlyList<SeriesTimerInfo> SeriesRules { get; private set; } = [];

    /// <summary>Why Live TV couldn't load, shown in every section.</summary>
    public string? ErrorMessage { get; private set; }

    private async Task LoadAsync()
    {
        try
        {
            var channels = Client.ChannelsAsync();
            var onNow = OrEmpty(Client.RecommendedProgramsAsync(limit: 30));
            var movies = OrEmpty(Client.RecommendedProgramsAsync(limit: 20, isMovie: true));
            var sports = OrEmpty(Client.RecommendedProgramsAsync(limit: 20, isSports: true));
            var kids = OrEmpty(Client.RecommendedProgramsAsync(limit: 20, isKids: true));
            var news = OrEmpty(Client.RecommendedProgramsAsync(limit: 20, isNews: true));
            Channels = LiveGuide.Order(await channels);
            OnNow = await onNow;
            Movies = await movies;
            Sports = await sports;
            Kids = await kids;
            News = await news;
            ErrorMessage = null;
        }
        catch (Exception error) when (IsServerError(error))
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            ErrorMessage = error.Message;
        }
    }

    private async Task LoadRecordingsAsync()
    {
        var recordings = OrEmpty(Client.RecordingsAsync());
        var timers = OrEmpty(Client.TimersAsync());
        var rules = OrEmpty(Client.SeriesTimersAsync());
        Recordings = await recordings;
        Timers = (await timers).OrderBy(t => JellyfinDate.Parse(t.StartDate) ?? DateTimeOffset.MaxValue).ToList();
        SeriesRules = await rules;
    }

    private static async Task<IReadOnlyList<T>> OrEmpty<T>(Task<IReadOnlyList<T>> loading)
    {
        try { return await loading; }
        catch (Exception error) when (IsServerError(error)) { return []; }
    }

    private static bool IsServerError(Exception error) =>
        error is JellyfinException or HttpRequestException or TimeoutException;

    /// <summary>Back on the tab: what's on has moved on, and recordings may have changed.</summary>
    public void Refresh()
    {
        Loaded = LoadAsync();
        RecordingsRefresh();
    }

    /// <summary>A recording was made or cancelled: Recordings and the guide catch up.</summary>
    public void RecordingsRefresh()
    {
        RecordingsLoaded = LoadRecordingsAsync();
        foreach (var page in pages.Values.OfType<IRefreshable>()) page.Refresh();
    }

    // ---- Sections ----

    [ObservableProperty] public partial IReadOnlyList<Choice> Sections { get; private set; } = [];
    [ObservableProperty] public partial ViewModelBase? CurrentSection { get; private set; }

    private void Show(Section chosen)
    {
        section = chosen;
        if (!pages.TryGetValue(chosen, out var page))
        {
            page = chosen switch
            {
                Section.Guide => new GuideViewModel(this),
                Section.Channels => new LiveChannelsViewModel(navigator, this),
                Section.Recordings => new LiveRecordingsViewModel(navigator, this),
                _ => new LiveOnNowViewModel(navigator, this),
            };
            pages[chosen] = page;
        }
        CurrentSection = page;
        Sections = SectionTitles.Select(s => new Choice(s.Title, s.Section == section, new RelayCommand(() => Show(s.Section)))).ToList();
    }

    // ---- Actions ----

    public void PlayChannel(string channelId) => navigator.Play(new PlaybackQueueItem { ItemId = channelId });

    public void OpenProgram(BaseItem program) => navigator.Push(new ProgramViewModel(navigator, this, program));

    public void OpenSeriesRule(string ruleId) => navigator.Push(new SeriesTimerViewModel(navigator, ruleId, RecordingsRefresh));

    public bool IsFavorite(string channelId) => Channels.FirstOrDefault(c => c.Id == channelId)?.UserData?.IsFavorite == true;

    /// <summary>Marks a channel in place, without re-sorting so nothing jumps, and tells the server.</summary>
    public async void SetFavorite(string channelId, bool favorite)
    {
        Channels = Channels.Select(c => c.Id == channelId ? c with { UserData = (c.UserData ?? new UserItemData()) with { IsFavorite = favorite } } : c).ToList();
        try { await Client.SetFavoriteAsync(channelId, favorite); }
        catch (Exception error) when (IsServerError(error)) { }
        if (pages.GetValueOrDefault(Section.OnNow) is IRefreshable onNow) onNow.Refresh();
    }
}

/// <summary>On Now: what's airing, by kind, then the favourite channels.</summary>
public sealed class LiveOnNowViewModel : BrowsePageViewModel
{
    private readonly LiveTVViewModel live;

    public LiveOnNowViewModel(INavigator navigator, LiveTVViewModel live) : base(navigator)
    {
        this.live = live;
        _ = LoadAsync();
    }

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        await live.Loaded;
        if (live.ErrorMessage is { } error) return [new StatusRow("Live TV isn't available", error)];
        var rows = new List<BrowseRow>();
        if (live.OnNow.Count == 0 && live.Movies.Count == 0 && live.Sports.Count == 0)
            rows.Add(new StatusRow("Nothing on right now", "The guide has no programmes airing at the moment. Check Channels to tune in directly."));
        foreach (var (title, programs) in new[] { ("On Now", live.OnNow), ("Movies", live.Movies), ("Sports", live.Sports), ("Kids", live.Kids), ("News", live.News) })
        {
            if (programs.Count > 0) rows.Add(new ShelfRow(title, programs.Select(p => (object)new ProgramCard(live, p)).ToList(), CardStyle.Program));
        }
        var favorites = live.Channels.Where(c => c.UserData?.IsFavorite == true).Select(c => (object)new ChannelCard(live, c)).ToList();
        if (favorites.Count > 0) rows.Add(new ShelfRow("Favorite Channels", favorites, CardStyle.Channel));
        return rows;
    }
}

/// <summary>Every channel, as a grid of logos.</summary>
public sealed class LiveChannelsViewModel : BrowsePageViewModel
{
    private readonly LiveTVViewModel live;
    private IReadOnlyList<object>? cards;

    public LiveChannelsViewModel(INavigator navigator, LiveTVViewModel live) : base(navigator)
    {
        this.live = live;
        _ = LoadAsync();
    }

    protected override bool HasGrid => true;
    public override CardStyle GridStyle => CardStyle.Channel;
    protected override IReadOnlyList<object>? LocalCards => cards;
    protected override bool IndexesByName => false;
    public override bool ShowsSort => false;

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        await live.Loaded;
        if (live.ErrorMessage is { } error) return [new StatusRow("Live TV isn't available", error)];
        cards = live.Channels.Select(c => (object)new ChannelCard(live, c)).ToList();
        return cards.Count == 0 ? [new StatusRow("No channels", "Add a tuner or M3U source in the Jellyfin dashboard.")] : [];
    }
}

/// <summary>What's been recorded, what's scheduled, and the series rules.</summary>
public sealed class LiveRecordingsViewModel : BrowsePageViewModel
{
    private readonly LiveTVViewModel live;

    public LiveRecordingsViewModel(INavigator navigator, LiveTVViewModel live) : base(navigator)
    {
        this.live = live;
        _ = LoadAsync();
    }

    protected override async Task<IReadOnlyList<BrowseRow>> LoadTopAsync()
    {
        await live.RecordingsLoaded;
        if (live.Recordings.Count == 0 && live.Timers.Count == 0 && live.SeriesRules.Count == 0)
            return [new StatusRow("No recordings", "Open a programme from the guide and choose Record to schedule one.")];
        var rows = new List<BrowseRow>();
        if (LandscapeShelf("Recorded", live.Recordings) is { } recorded) rows.Add(recorded);
        if (live.Timers.Count > 0) rows.Add(new ShelfRow("Scheduled", live.Timers.Select(t => (object)new TimerCard(live, t)).ToList(), CardStyle.Timer));
        if (live.SeriesRules.Count > 0) rows.Add(new ShelfRow("Series Rules", live.SeriesRules.Select(r => (object)new SeriesRuleCard(live, r)).ToList(), CardStyle.SeriesRule));
        return rows;
    }
}
