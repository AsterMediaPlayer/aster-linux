using System;
using System.Linq;
using Aster.Core.LiveTV;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aster.Desktop.Controls;

namespace Aster.Desktop.Views;

/// <summary>
/// Tells the guide how wide its timeline is, which rows have come into view
/// (so their listings load), and moves through time with Shift and the
/// wheel, or a sideways swipe on a touchpad.
/// </summary>
public partial class GuideView : UserControl
{
    public GuideView()
    {
        InitializeComponent();
        Timeline.SizeChanged += (_, e) => Model?.SetTimelineWidth(e.NewSize.Width);
        RowList.ContainerPrepared += (_, e) =>
        {
            if (Model is { } guide && e.Index < guide.Rows.Count) guide.RowShown(guide.Rows[e.Index]);
        };
        // The programme under the pointer, or with the keyboard on it, fills the panel.
        RowList.AddHandler(PointerMovedEvent, (_, e) => Highlight(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        RowList.AddHandler(GotFocusEvent, (_, e) => Highlight(e.Source), RoutingStrategies.Bubble);
        RowList.AddHandler(KeyDownEvent, OnRowKeyDown, RoutingStrategies.Tunnel);
        RowList.AddHandler(KeyDownEvent, OnRowUpDown, RoutingStrategies.Tunnel);
        RowList.AddHandler(KeyDownEvent, OnFirstRowUp, RoutingStrategies.Tunnel);
        GroupList.AddHandler(KeyDownEvent, OnGroupsDown, RoutingStrategies.Tunnel);
        Scroller.AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            var sideways = e.Delta.X != 0 ? -e.Delta.X : e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -e.Delta.Y : 0;
            if (sideways == 0 || Model is not { } guide) return;
            // A notch is a quarter hour.
            guide.Move(sideways * 15);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private GuideViewModel? Model => DataContext as GuideViewModel;

    /// <summary>
    /// Left and Right go to the channel's programme before or after, moving
    /// the guide through time when it's off the edge. Left from the first
    /// programme goes on to the channel, and from there the sidebar.
    /// </summary>
    private void OnRowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right) || e.KeyModifiers != KeyModifiers.None || Model is not { } guide) return;
        if (e.Source is not Button { DataContext: GuideCell cell } button) return;
        if (RowOf(button) is not var (container, row) || row.Beside(cell.Program, e.Key == Key.Right ? 1 : -1) is not { } target) return;
        guide.Reveal(target);
        e.Handled = true;
        // The row lays its cells out again once the window moves.
        Dispatcher.UIThread.Post(() =>
        {
            var next = container.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.DataContext is GuideCell c && c.Program.Id == target.Id);
            if (next is not null) KeyFocus.Move(next);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Up and Down go to the channel above or below, onto what's on there at
    /// the time the keyboard is at, as the Apple TV's guide does; from a
    /// channel's name, to the next one's. Down on the last channel stays put.
    /// (Left to Avalonia, Down could leave the guide for Edit Channels.) Up
    /// from the first row is the group filter's, or the top bar's.
    /// </summary>
    private void OnRowUpDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None || Model is not { } guide) return;
        if (e.Source is not Visual source || RowOf(source) is not var (_, row)) return;
        var index = guide.Rows.ToList().IndexOf(row) + (e.Key == Key.Down ? 1 : -1);
        if (index < 0) return;
        e.Handled = true;
        if (index >= guide.Rows.Count) return;
        var target = guide.Rows[index];
        // The time the keyboard is at: the programme's start, or the window's when that's scrolled off.
        DateTimeOffset? at = e.Source is Button { DataContext: GuideCell cell }
            ? Max(LiveGuide.Slot(cell.Program, guide.GuideStart).Start, guide.WindowStart)
            : null;
        RowList.ScrollIntoView(index);
        Dispatcher.UIThread.Post(() =>
        {
            if (RowList.ContainerFromIndex(index) is not { } container) return;
            var buttons = container.GetVisualDescendants().OfType<Button>().Where(KeyFocus.CanFocus).ToList();
            Button? next = null;
            if (at is { } time)
            {
                var program = target.Cells.FirstOrDefault(c => Covers(c, time))?.Program ?? target.Cells.FirstOrDefault()?.Program;
                next = buttons.FirstOrDefault(b => b.DataContext is GuideCell c && c.Program.Id == program?.Id)
                    // A row still loading, or without listings: its placeholder.
                    ?? buttons.LastOrDefault(b => b.DataContext is GuideRowViewModel);
            }
            // From a channel's name: the next channel's (the row's first button).
            next ??= buttons.FirstOrDefault();
            if (next is not null) KeyFocus.Move(next);
        }, DispatcherPriority.Loaded);

        bool Covers(GuideCell c, DateTimeOffset time)
        {
            var (start, end) = LiveGuide.Slot(c.Program, guide.GuideStart);
            return start <= time && time < end;
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>Up from the first row goes to the group filter, on the group picked, as the Apple TV's guide has it.</summary>
    private void OnFirstRowUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Up || e.KeyModifiers != KeyModifiers.None || Model is not { HasGroups: true } guide) return;
        if (e.Source is not Visual source || RowOf(source) is not var (_, row) || guide.Rows.FirstOrDefault() != row) return;
        var picked = guide.Groups.ToList().FindIndex(g => g.IsSelected);
        if (GroupList.ContainerFromIndex(System.Math.Max(0, picked)) is { } chip && KeyFocus.FirstIn(chip) is { } button)
        {
            KeyFocus.Move(button);
            e.Handled = true;
        }
    }

    /// <summary>Down from the group filter goes to the first row, on what's airing.</summary>
    private void OnGroupsDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Down || e.KeyModifiers != KeyModifiers.None) return;
        if (RowList.ContainerFromIndex(0) is { } first && KeyFocus.FirstIn(first) is { } target)
        {
            KeyFocus.Move(target);
            e.Handled = true;
        }
    }

    /// <summary>The row a cell is in: its container in the list, and its model.</summary>
    private (Control Container, GuideRowViewModel Model)? RowOf(Visual from)
    {
        for (var node = from; node is not null; node = node.GetVisualParent())
        {
            if (node is Control { DataContext: GuideRowViewModel row } container && RowList.ContainerFromItem(row) == container) return (container, row);
        }
        return null;
    }

    private void Highlight(object? source)
    {
        for (var node = source as StyledElement; node is not null; node = node.Parent)
        {
            if (node.DataContext is GuideCell cell)
            {
                Model?.Highlight(cell.Program);
                return;
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Model?.Shown(true);
        if (Model is { } guide && Timeline.Bounds.Width > 0) guide.SetTimelineWidth(Timeline.Bounds.Width);
        // Opened again: the top channel, at the time it is now, where the keyboard starts (KeyFocus.StartsFresh).
        Scroller.Offset = default;
        if (Model?.NowCommand.CanExecute(null) == true) Model.NowCommand.Execute(null);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Model?.Shown(false);
        base.OnDetachedFromVisualTree(e);
    }

    private GuideViewModel? watched;

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (watched is not null) watched.PropertyChanged -= ModelChanged;
        watched = Model;
        if (watched is not null) watched.PropertyChanged += ModelChanged;
        if (Model is { } guide && IsAttachedToVisualTree()) guide.Shown(true);
    }

    /// <summary>Another group picked: its first channel, at the top.</summary>
    private void ModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GuideViewModel.Rows)) Scroller.Offset = default;
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}
