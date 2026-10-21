using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
