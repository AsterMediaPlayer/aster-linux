using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.LiveTV;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// One programme in a guide row, placed by its time: <see cref="Margin"/>
/// puts it at its start, and <see cref="Inset"/> keeps its title in view
/// when its start has scrolled off to the left.
/// </summary>
public sealed record GuideCell(
    BaseItem Program,
    string Title,
    string Subtitle,
    Thickness Margin,
    double Width,
    Thickness Inset,
    bool IsAiring,
    bool IsRecording,
    double AiredWidth,
    string AccessibleName,
    IRelayCommand Open);

/// <summary>A half-hour mark along the top of the guide.</summary>
public sealed record GuideTick(string Text, Thickness Margin);

/// <summary>A row's channel, as the guide shows it.</summary>
public sealed record GuideChannel(string Id, string? Number, string Name, Uri? Logo, bool IsFavorite);

/// <summary>
/// Where a guide's channels and listings come from: Live TV's, fetched from
/// the server, or Channels', worked out from their schedules.
/// </summary>
public interface IGuideSource
{
    /// <summary>The channels, loading or loaded.</summary>
    Task Loaded { get; }

    /// <summary>Why there's no guide (it couldn't load, or there are no channels); null when there is one.</summary>
    (string Title, string Detail)? Problem { get; }

    IReadOnlyList<GuideChannel> GuideChannels { get; }

    /// <summary>How far ahead the guide reaches: a day for Live TV, a week for Channels.</summary>
    TimeSpan GuideLength { get; }

    /// <summary>Whether a channel can be made a favourite from its cell.</summary>
    bool CanFavorite { get; }

    Task<IReadOnlyList<BaseItem>> ProgramsAsync(string channelId, DateTimeOffset from, DateTimeOffset to);

    /// <summary>A programme's full record for the panel (its picture and overview), which a listing can leave out.</summary>
    Task<BaseItem> DetailAsync(BaseItem program);

    /// <summary>The panel's picture of a programme.</summary>
    Uri? PanelImageUrl(BaseItem program);

    void OpenProgram(BaseItem program);
    void PlayChannel(string channelId);
    void SetFavorite(string channelId, bool favorite);
}

/// <summary>A day the guide reaches into, for its day picker.</summary>
public sealed record GuideDay(string Title, IRelayCommand Select);

/// <summary>A channel's row: its cell, then its programmes, loaded when the row first shows.</summary>
public sealed partial class GuideRowViewModel(GuideViewModel guide, IGuideSource source, GuideChannel channel) : ObservableObject
{
    private IReadOnlyList<BaseItem>? programs;
    private bool loading;

    public string ChannelId => channel.Id;
    public Uri? Logo => channel.Logo;
    public string? Number => channel.Number;
    public string Name => channel.Name;
    public bool CanFavorite => source.CanFavorite;
    public string AccessibleName => $"Watch {string.Join(" ", new[] { channel.Number, channel.Name }.OfType<string>())}";

    [ObservableProperty] public partial IReadOnlyList<GuideCell> Cells { get; private set; } = [];

    /// <summary>"No guide data", or null while there's listings (or they're loading).</summary>
    [ObservableProperty] public partial string? Placeholder { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FavoriteLabel))]
    public partial bool IsFavorite { get; set; } = channel.IsFavorite;

    public string FavoriteLabel => IsFavorite ? "Remove from Favorites" : "Add to Favorites";

    [RelayCommand] private void Play() => source.PlayChannel(channel.Id);

    [RelayCommand]
    private void ToggleFavorite()
    {
        IsFavorite = !IsFavorite;
        source.SetFavorite(channel.Id, IsFavorite);
    }

    /// <summary>Starts loading the listings, once; a failed load is tried again the next time the row shows.</summary>
    public async void EnsureLoaded(bool reload = false)
    {
        if (loading || (programs is not null && !reload)) return;
        loading = true;
        try
        {
            var loaded = await source.ProgramsAsync(channel.Id, guide.GuideStart, guide.GuideEnd);
            programs = loaded.Where(p => p.End is { } end && end > guide.GuideStart.AddMinutes(1)).ToList();
            IsLoading = false;
            Layout();
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
        finally { loading = false; }
    }

    /// <summary>Places the programmes in the guide's window.</summary>
    public void Layout()
    {
        if (programs is null) return;
        var window = guide.WindowStart;
        var windowEnd = window.AddMinutes(guide.VisibleMinutes);
        var perMinute = GuideViewModel.PixelsPerMinute;
        var now = DateTimeOffset.UtcNow;
        var cells = new List<GuideCell>();
        foreach (var program in programs)
        {
            var (start, end) = LiveGuide.Slot(program, guide.GuideStart);
            if (end <= window || start >= windowEnd) continue;
            var width = Math.Max(GuideViewModel.MinimumCellWidth, (end - start).TotalMinutes * perMinute - GuideViewModel.CellGap);
            var left = (start - window).TotalMinutes * perMinute;
            var hidden = Math.Max(0, -left);
            var inset = Math.Min(hidden, Math.Max(0, width - GuideViewModel.MinimumVisibleCell));
            var airing = program.IsAiringNow;
            var aired = airing ? Math.Clamp((now - start) / (end - start), 0, 1) * width : 0;
            var subtitle = program.EpisodeTitle is { } episode && episode != program.Name ? episode : LiveTVFormat.TimeRange(program.Start, program.End);
            cells.Add(new GuideCell(
                program,
                program.DisplayTitle,
                subtitle,
                new Thickness(left, 0, 0, 0),
                width,
                new Thickness(14 + inset, 10, 14, 10),
                airing,
                program.TimerId is not null || program.SeriesTimerId is not null,
                aired,
                string.Join(", ", new[] { program.Name, program.EpisodeTitle, LiveTVFormat.TimeRange(program.Start, program.End) }.OfType<string>()),
                new RelayCommand(() => source.OpenProgram(program))));
        }
        Cells = cells;
        Placeholder = cells.Count == 0 ? "No guide data" : null;
        guide.RowLaidOut(this);
    }
}

/// <summary>
/// The programme guide: a row per channel, with its programmes laid out
/// along a timeline that starts at the current half hour and reaches a day
/// ahead (a week for Channels, with a day picker). The window of time in
/// view moves with Earlier and Later (or Shift and the mouse wheel). A port
/// of the Apple app's GuideView.
/// </summary>
public sealed partial class GuideViewModel : ViewModelBase, IRefreshable
{
    public const double PixelsPerMinute = 7;
    public const double CellGap = 6;
    public const double MinimumCellWidth = 24;

    /// <summary>A cell keeps at least this much of itself readable when its start is scrolled out of view.</summary>
    public const double MinimumVisibleCell = 160;

    private readonly IGuideSource source;
    private readonly DispatcherTimer minute = new() { Interval = TimeSpan.FromMinutes(1) };
    private double timelineWidth = 900;

    public GuideViewModel(IGuideSource source)
    {
        this.source = source;
        GuideStart = LiveGuide.FloorToHalfHour(DateTimeOffset.UtcNow);
        GuideEnd = GuideStart + source.GuideLength;
        WindowStart = GuideStart;
        Days = MakeDays();
        // The red line and what's on move on as the clock does.
        minute.Tick += (_, _) => Relayout();
        _ = LoadAsync();
    }

    /// <summary>On screen: the clock's moved on since it was last shown, and keeps moving.</summary>
    public void Shown(bool shown)
    {
        if (!shown)
        {
            minute.Stop();
            return;
        }
        minute.Start();
        Relayout();
    }

    public DateTimeOffset GuideStart { get; }
    public DateTimeOffset GuideEnd { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EarlierCommand), nameof(NowCommand))]
    [NotifyPropertyChangedFor(nameof(DayLabel), nameof(CanJumpToNow))]
    public partial DateTimeOffset WindowStart { get; private set; }

    public double VisibleMinutes => timelineWidth / PixelsPerMinute;

    [ObservableProperty] public partial IReadOnlyList<GuideRowViewModel> Rows { get; private set; } = [];
    [ObservableProperty] public partial IReadOnlyList<GuideTick> Ticks { get; private set; } = [];
    [ObservableProperty] public partial Thickness NowMargin { get; private set; }
    [ObservableProperty] public partial bool ShowsNow { get; private set; }
    [ObservableProperty] public partial string? MessageTitle { get; private set; }
    [ObservableProperty] public partial string? MessageDetail { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;

    public string DayLabel => LiveTVFormat.DayLabel(WindowStart, DateTimeOffset.Now);

    /// <summary>The window has moved off the current half hour.</summary>
    public bool CanJumpToNow => Math.Abs((WindowStart - CurrentWindow).TotalMinutes) >= 1;

    private DateTimeOffset CurrentWindow => LiveGuide.Clamp(LiveGuide.FloorToHalfHour(DateTimeOffset.UtcNow), VisibleMinutes, GuideStart, GuideEnd);

    private async Task LoadAsync()
    {
        await source.Loaded;
        IsLoading = false;
        if (source.Problem is var (title, detail))
        {
            (MessageTitle, MessageDetail) = (title, detail);
            return;
        }
        Rows = source.GuideChannels.Select(c => new GuideRowViewModel(this, source, c)).ToList();
        Relayout();
    }

    // ---- The panel: what's highlighted, or what's on now on the first row ----

    private readonly Dictionary<string, BaseItem> details = [];
    private BaseItem? highlighted;

    [ObservableProperty] public partial Uri? PanelImage { get; private set; }
    [ObservableProperty] public partial string? PanelTitle { get; private set; }
    [ObservableProperty] public partial string? PanelEpisode { get; private set; }
    [ObservableProperty] public partial string? PanelOverview { get; private set; }

    /// <summary>The pointer or the keyboard is on a programme: the panel shows it.</summary>
    public void Highlight(BaseItem program)
    {
        if (highlighted?.Id == program.Id) return;
        highlighted = program;
        ShowPanel(program);
    }

    /// <summary>Nothing highlighted yet: what's on now on the first row, once its listings are in.</summary>
    internal void RowLaidOut(GuideRowViewModel row)
    {
        if (highlighted is not null || Rows.Count == 0 || Rows[0] != row) return;
        if (row.Cells.FirstOrDefault(c => c.IsAiring)?.Program is { } airing) ShowPanel(airing);
    }

    private async void ShowPanel(BaseItem program)
    {
        Fill(details.GetValueOrDefault(program.Id) ?? program, program);
        if (details.ContainsKey(program.Id)) return;
        try
        {
            var detail = await source.DetailAsync(program);
            details[program.Id] = detail;
            // Only if it's still the one to show.
            if (highlighted is null || highlighted.Id == program.Id) Fill(detail, program);
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
    }

    /// <summary>
    /// The title, "S3, E12 · Episode Name" (or whichever parts there are),
    /// the overview, and the picture: the detail's, or the listing's when the
    /// detail leaves its picture out.
    /// </summary>
    private void Fill(BaseItem detail, BaseItem listing)
    {
        PanelTitle = detail.DisplayTitle;
        var parts = new List<string>();
        if (detail.IndexNumber is { } episode) parts.Add(detail.ParentIndexNumber is { } season ? $"S{season}, E{episode}" : $"Episode {episode}");
        if (detail.EpisodeTitle is { } name && name != detail.Name) parts.Add(name);
        PanelEpisode = parts.Count > 0 ? string.Join(" · ", parts) : null;
        PanelOverview = detail.Overview;
        PanelImage = source.PanelImageUrl(detail.ImageTags is { Count: > 0 } ? detail : listing);
    }

    /// <summary>The days a guide of several reaches into; none for a guide of a day, which has no day picker.</summary>
    public IReadOnlyList<GuideDay> Days { get; }

    public bool HasDays => Days.Count > 0;

    private IReadOnlyList<GuideDay> MakeDays()
    {
        if (GuideEnd - GuideStart <= TimeSpan.FromHours(26)) return [];
        var days = new List<GuideDay>();
        var now = DateTimeOffset.Now;
        for (var day = GuideStart.ToLocalTime().Date; day < GuideEnd.ToLocalTime().DateTime; day = day.AddDays(1))
        {
            var start = new DateTimeOffset(day, TimeZoneInfo.Local.GetUtcOffset(day));
            days.Add(new GuideDay(LiveTVFormat.DayLabel(start, now), new RelayCommand(() => JumpTo(start))));
        }
        return days;
    }

    /// <summary>Another day at the time of day it is now, as a TV guide's day buttons do; today goes back to now.</summary>
    private void JumpTo(DateTimeOffset day)
    {
        var now = DateTimeOffset.Now;
        var target = day.Date == now.Date ? now : day + now.TimeOfDay;
        WindowStart = LiveGuide.Clamp(LiveGuide.FloorToHalfHour(target), VisibleMinutes, GuideStart, GuideEnd);
        Relayout();
    }

    /// <summary>A row came into view: its listings load, and the next few rows' too, so scrolling rarely meets one loading.</summary>
    public void RowShown(GuideRowViewModel row)
    {
        var index = Rows.ToList().IndexOf(row);
        if (index < 0) return;
        foreach (var next in Rows.Skip(index).Take(10)) next.EnsureLoaded();
    }

    /// <summary>The view measured the timeline: as many minutes as fit are in view.</summary>
    public void SetTimelineWidth(double width)
    {
        if (width <= 0 || Math.Abs(width - timelineWidth) < 1) return;
        timelineWidth = width;
        WindowStart = LiveGuide.Clamp(WindowStart, VisibleMinutes, GuideStart, GuideEnd);
        Relayout();
    }

    /// <summary>Moves the window by <paramref name="minutes"/>, staying inside the guide.</summary>
    public void Move(double minutes)
    {
        var moved = LiveGuide.Clamp(WindowStart.AddMinutes(minutes), VisibleMinutes, GuideStart, GuideEnd);
        if (moved == WindowStart) return;
        WindowStart = moved;
        Relayout();
    }

    [RelayCommand(CanExecute = nameof(CanGoEarlier))]
    private void Earlier() => Move(-60);

    private bool CanGoEarlier() => WindowStart > GuideStart;

    [RelayCommand] private void Later() => Move(60);

    [RelayCommand(CanExecute = nameof(CanJumpToNow))]
    private void Now()
    {
        WindowStart = CurrentWindow;
        Relayout();
    }

    private void Relayout()
    {
        var perMinute = PixelsPerMinute;
        var ticks = new List<GuideTick>();
        for (var tick = GuideStart; tick < GuideEnd; tick = tick.AddMinutes(30))
        {
            var left = (tick - WindowStart).TotalMinutes * perMinute;
            if (left < -30 * perMinute || left > timelineWidth) continue;
            ticks.Add(new GuideTick(LiveTVFormat.Time(tick), new Thickness(left, 0, 0, 0)));
        }
        Ticks = ticks;
        var now = (DateTimeOffset.UtcNow - WindowStart).TotalMinutes * perMinute;
        ShowsNow = now >= 0 && now <= timelineWidth;
        NowMargin = new Thickness(now, 0, 0, 0);
        OnPropertyChanged(nameof(CanJumpToNow));
        NowCommand.NotifyCanExecuteChanged();
        foreach (var row in Rows) row.Layout();
    }

    /// <summary>A recording was made or cancelled: the rows that have loaded fetch their listings again, to show it.</summary>
    public void Refresh()
    {
        foreach (var row in Rows) row.EnsureLoaded(reload: true);
    }
}
