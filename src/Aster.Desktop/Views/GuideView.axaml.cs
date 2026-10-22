using System.Linq;
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
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Model?.Shown(false);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (Model is { } guide && IsAttachedToVisualTree()) guide.Shown(true);
    }

    private bool IsAttachedToVisualTree() => TopLevel.GetTopLevel(this) is not null;
}
