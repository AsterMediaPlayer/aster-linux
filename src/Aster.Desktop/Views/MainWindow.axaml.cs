using Aster.Desktop.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Aster.Desktop.Views;

/// <summary>
/// Back, wherever the keyboard is (or isn't, after the page it was on went):
/// the mouse's back button, Alt+← and Backspace go back a page.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsXButton1Pressed && GoBack()) e.Handled = true;
        }, RoutingStrategies.Tunnel);
        // Lock After: away is the window in the background or minimized.
        Deactivated += (_, _) =>
        {
            (DataContext as MainViewModel)?.NoteLeft();
            // A video pauses as Aster goes behind another window (Steam's overlay, another app), to
            // carry on from there on coming back; songs and books play on. Not in gamescope, whose
            // one window can be told it's not focused while it's the screen.
            if (!Aster.Desktop.Platform.SteamSession.InGamescope) Aster.Desktop.Audio.VideoMedia.Current?.Run(Aster.Desktop.Audio.MediaCommand.Pause);
        };
        Activated += (_, _) => (DataContext as MainViewModel)?.CheckLock();
        // Anything pressed or moved (a controller's buttons come as keys): not idle, for the profile's PIN.
        AddHandler(KeyDownEvent, (_, _) => (DataContext as MainViewModel)?.NoteInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, (_, _) => (DataContext as MainViewModel)?.NoteInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => (DataContext as MainViewModel)?.NoteInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerWheelChangedEvent, (_, _) => (DataContext as MainViewModel)?.NoteInput(), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        // A video full screen over the pages: its keys are the player's alone.
        if (DataContext is MainViewModel { CurrentPage: ShellViewModel { Video.IsMini: false } }) return;
        // The song or book: Space (outside a text box or button) or the
        // keyboard's play key toggles it, its other media keys move through
        // it, and Esc puts its player away.
        if (DataContext is MainViewModel { CurrentPage: ShellViewModel { Audio: { IsLoaded: true } audio } })
        {
            if (e.Key == Key.MediaPlayPause || (e.Key == Key.Space && e.Source is not (TextBox or Button)))
            {
                audio.Toggle();
                e.Handled = true;
                return;
            }
            // The keyboard's next and previous keys: the song, or the book's chapter.
            if (e.Key is Key.MediaNextTrack or Key.MediaPreviousTrack)
            {
                var next = e.Key == Key.MediaNextTrack;
                if (audio.ShowsBookPlayer)
                {
                    if (next) audio.Books.NextChapter();
                    else audio.Books.PreviousChapter();
                }
                else if (next) audio.Music.Next();
                else audio.Music.Previous();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.MediaStop)
            {
                audio.Stop();
                e.Handled = true;
                return;
            }
            if (e.Key is Key.Escape or Key.BrowserBack && audio.IsNowPlayingShown)
            {
                audio.IsNowPlayingShown = false;
                e.Handled = true;
                return;
            }
        }
        // Esc puts the sidebar away, before anything else it does.
        if (e.Key == Key.Escape && DataContext is MainViewModel { CurrentPage: ShellViewModel { IsSidebarOpen: true } open })
        {
            open.IsSidebarOpen = false;
            e.Handled = true;
            return;
        }
        // Ctrl+F: the Search tab, its box ready for typing.
        if (e.Key == Key.F && e.KeyModifiers == KeyModifiers.Control && DataContext is MainViewModel { CurrentPage: ShellViewModel shell })
        {
            shell.ShowTab(AppTab.Search);
            e.Handled = true;
            return;
        }
        var back = e.Key == Key.BrowserBack
            || (e.Key == Key.Left && e.KeyModifiers == KeyModifiers.Alt)
            || (e.Key == Key.Back && e.Source is not TextBox);
        if (back && GoBack()) e.Handled = true;
    }

    private bool GoBack()
    {
        if (DataContext is not MainViewModel { CurrentPage: ShellViewModel { CanGoBack: true } shell }) return false;
        shell.Back();
        return true;
    }
}
