using System.Linq;
using Aster.Desktop.Controls;
using Aster.Desktop.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Aster.Desktop.Views;

/// <summary>
/// The search box takes the keys whenever the tab is shown; Down (or Enter)
/// from it goes to the results, and Up from the first row of them comes back.
/// From the couch (gamescope, Big Picture) the keyboard on the screen does
/// instead: the page starts on its first key, the box only shows what's
/// typed, Down from its bottom row goes to the results, and Up from their
/// first row comes back to the nearest key.
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
        if (!SteamSession.IsCouch) return;
        Keys.IsVisible = true;
        Box.Focusable = false;
        KeyFocus.SetIsPreferred(Box, false);
        KeyFocus.SetIsPreferred(Keys.First, true);
        Keys.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Down || e.KeyModifiers != KeyModifiers.None || KeyFocus.FirstIn(Results) is not { } first) return;
            KeyFocus.Move(first);
            e.Handled = true;
        });
        Results.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Up || e.KeyModifiers != KeyModifiers.None || e.Source is not Visual from) return;
            // Only from the first row of results.
            double? Top(Visual v) => v.TranslatePoint(default, Results)?.Y;
            var tops = Results.GetVisualDescendants().OfType<InputElement>().Where(KeyFocus.CanFocus).Select(v => Top(v) ?? double.MaxValue);
            if (Top(from) is not { } top || !tops.Any() || top > tops.Min() + 1) return;
            Keys.FocusBottom(from);
            e.Handled = true;
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

    private void FocusBox() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (SteamSession.IsCouch) KeyFocus.Move(Keys.First);
        else Box.Focus();
    }, Avalonia.Threading.DispatcherPriority.Loaded);
}
