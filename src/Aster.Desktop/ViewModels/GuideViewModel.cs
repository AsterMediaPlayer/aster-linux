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

/// <summary>A channel's row: its cell, then its programmes, loaded when the row first shows.</summary>
public sealed partial class GuideRowViewModel(GuideViewModel guide, LiveTVViewModel live, BaseItem channel) : ObservableObject
{
    private IReadOnlyList<BaseItem>? programs;
    private bool loading;

    public string ChannelId => channel.Id;
    public Uri? Logo { get; } = live.Client.ChannelLogoUrl(channel, width: 200);
    public string? Number => channel.ChannelNumber;
    public string Name => channel.DisplayTitle;
    public string AccessibleName => $"Watch {string.Join(" ", new[] { channel.ChannelNumber, channel.DisplayTitle }.OfType<string>())}";

    [ObservableProperty] public partial IReadOnlyList<GuideCell> Cells { get; private set; } = [];

    /// <summary>"No guide data", or null while there's listings (or they're loading).</summary>
    [ObservableProperty] public partial string? Placeholder { get; private set; }

    [ObservableProperty] public partial bool IsLoading { get; private set; } = true;

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

    /// <summary>Starts loading the listings, once; a failed load is tried again the next time the row shows.</summary>
    public async void EnsureLoaded(bool reload = false)
    {
        if (loading || (programs is not null && !reload)) return;
        loading = true;
        try
        {
            var loaded = await live.Client.ProgramsAsync(channel.Id, guide.GuideStart, guide.GuideEnd);
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
                program.DisplayTitle,
                subtitle,
                new Thickness(left, 0, 0, 0),
                width,
                new Thickness(14 + inset, 10, 14, 10),
                airing,
                program.TimerId is not null || program.SeriesTimerId is not null,
                aired,
                string.Join(", ", new[] { program.Name, program.EpisodeTitle, LiveTVFormat.TimeRange(program.Start, program.End) }.OfType<string>()),
                new RelayCommand(() => live.OpenProgram(program))));
        }
        Cells = cells;
        Placeholder = cells.Count == 0 ? "No guide data" : null;
    }
}

/// <summary>
/// The programme guide: a row per channel, with its programmes laid out
/// along a timeline that starts at the current half hour and reaches a day
/// ahead. The window of time in view moves with Earlier and Later (or
/// Shift and the mouse wheel). A port of the Apple app's GuideView.
/// </summary>
public sealed partial class GuideViewModel : ViewModelBase, IRefreshable
{
    public const double PixelsPerMinute = 6;
    public const double CellGap = 6;
    public const double MinimumCellWidth = 24;

    /// <summary>A cell keeps at least this much of itself readable when its start is scrolled out of view.</summary>
    public const double MinimumVisibleCell = 140;

    private readonly LiveTVViewModel live;
    private readonly DispatcherTimer minute = new() { Interval = TimeSpan.FromMinutes(1) };
    private double timelineWidth = 900;

    public GuideViewModel(LiveTVViewModel live)
    {
        this.live = live;
        GuideStart = LiveGuide.FloorToHalfHour(DateTimeOffset.UtcNow);
        GuideEnd = GuideStart.AddHours(24);
        WindowStart = GuideStart;
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
        await live.Loaded;
        IsLoading = false;
        if (live.ErrorMessage is { } error)
        {
            (MessageTitle, MessageDetail) = ("Live TV isn't available", error);
            return;
        }
        if (live.Channels.Count == 0)
        {
            (MessageTitle, MessageDetail) = ("No channels", "Add a tuner or M3U source in the Jellyfin dashboard.");
            return;
        }
        Rows = live.Channels.Select(c => new GuideRowViewModel(this, live, c)).ToList();
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
