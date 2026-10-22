using Aster.Desktop.Controls;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

/// <summary>
/// The search box takes the keys whenever the tab is shown; Down (or Enter)
/// from it goes to the results, and Up from the first row of them comes back.
/// </summary>
public partial class SearchView : UserControl
{
    public SearchView()
    {
        InitializeComponent();
        // Before the box, which keeps Down for its caret.
        Box.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Down or Key.Enter) || e.KeyModifiers != KeyModifiers.None) return;
            if (KeyFocus.FirstIn(Results) is { } first)
            {
                KeyFocus.Move(first);
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        FocusBox();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is Aster.Desktop.ViewModels.SearchViewModel model) model.FocusRequested += FocusBox;
    }

    private void FocusBox() => Avalonia.Threading.Dispatcher.UIThread.Post(() => Box.Focus(), Avalonia.Threading.DispatcherPriority.Loaded);
}
