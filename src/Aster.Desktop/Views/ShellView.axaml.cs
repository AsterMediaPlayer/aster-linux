using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Aster.Desktop.Controls;

namespace Aster.Desktop.Views;

/// <summary>
/// The collapsing sidebar, and the keyboard and remote, as on the Apple TV.
///
/// The pointer at the window's left edge or resting on the capsule slides
/// the sidebar out, as does the keyboard arriving in it; the pointer leaving
/// it, a click on the page, the keyboard leaving it, or Esc puts it away.
///
/// The arrows move between controls in that direction (XYFocus, on the
/// window). Left with nothing further left opens the sidebar on the tab
/// showing, and Right from the sidebar goes back to the page. Each page
/// starts on its main action (or first control), and Back returns to the
/// control the page was left on. Esc on a page opened from a tab goes back;
/// on a tab's own page it opens the sidebar, as the remote's Menu does.
/// </summary>
public partial class ShellView : UserControl
{
    /// <summary>A moment's grace, so a pointer passing over the capsule, or wavering at the sidebar's edge, doesn't flick it in and out.</summary>
    private readonly DispatcherTimer hoverOpen = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer leaveClose = new() { Interval = TimeSpan.FromMilliseconds(350) };

    /// <summary>Where each page had the keyboard, to go back to.</summary>
    private readonly ConditionalWeakTable<ViewModelBase, WeakReference<InputElement>> pageFocus = new();

    /// <summary>The control on the page the keyboard left for the sidebar.</summary>
    private WeakReference<InputElement>? beforeSidebar;

    /// <summary>The keyboard (or remote) was used last, rather than the pointer: a page then shows where it starts.</summary>
    private bool keyboardLast;

    /// <summary>
    /// The pointer hasn't moved since a key was pressed. A pointer left
    /// resting at the screen's left edge (gamescope parks it there; a couch
    /// mouse is pushed out of the way) finds itself over the sidebar's edge
    /// whenever the screen changes under it, and slid the sidebar out, and
    /// in, against the controller: only a pointer that moves opens or shuts it.
    /// </summary>
    private bool pointerResting = true;
    private Point? pointerAt;

    private ShellViewModel? watched;

    public ShellView()
    {
        InitializeComponent();
        hoverOpen.Tick += (_, _) => { hoverOpen.Stop(); Open(); };
        leaveClose.Tick += (_, _) =>
        {
            leaveClose.Stop();
            if (!pointerResting && !Sidebar.IsPointerOver && !HasFocusWithin(Sidebar)) Close();
        };

        Edge.PointerEntered += (_, _) => { if (!pointerResting) Open(); };
        Capsule.PointerEntered += (_, _) => { if (!pointerResting) hoverOpen.Start(); };
        Capsule.PointerExited += (_, _) => hoverOpen.Stop();
        Sidebar.PointerEntered += (_, _) => leaveClose.Stop();
        Sidebar.PointerExited += (_, _) => { if (!pointerResting) leaveClose.Start(); };
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            // Moved, not just found somewhere new as the screen changed under it.
            var at = e.GetPosition(this);
            if (pointerAt is { } was && (Math.Abs(at.X - was.X) > 2 || Math.Abs(at.Y - was.Y) > 2)) pointerResting = false;
            pointerAt = at;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        Scrim.PointerPressed += (_, e) =>
        {
            Close();
            e.Handled = true;
        };

        // The keyboard: tabbing into the sidebar slides it out; tabbing on out of it puts it away.
        Sidebar.GotFocus += (_, _) => Open();
        Sidebar.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!HasFocusWithin(Sidebar) && (pointerResting || !Sidebar.IsPointerOver)) Close();
        }, DispatcherPriority.Input);

        AddHandler(PointerPressedEvent, (_, _) => keyboardLast = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        // Remember where on the page the keyboard is, for coming back to it.
        Page.AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is InputElement element && Model?.CurrentPage is { } page) pageFocus.AddOrUpdate(page, new WeakReference<InputElement>(element));
        }, RoutingStrategies.Bubble);

        // Where the keyboard last was among the pages, sidebar and top row, to come back to from the video.
        AddHandler(GotFocusEvent, (_, e) =>
        {
            if (e.Source is InputElement element && !KeyFocus.IsWithin(element, VideoLayer)) lastShellFocus = new WeakReference<InputElement>(element);
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        DataContextChanged += (_, _) =>
        {
            if (watched is not null)
            {
                watched.PropertyChanged -= OnShellChanged;
                watched.NowPlaying.PropertyChanged -= OnNowPlayingChanged;
            }
            watched = Model;
            if (watched is not null)
            {
                watched.PropertyChanged += OnShellChanged;
                watched.NowPlaying.PropertyChanged += OnNowPlayingChanged;
            }
            ShowPage();
        };
    }

    // ---- The video player ----

    private PlayerViewModel? watchedVideo;
    private bool videoWasMini;
    private WeakReference<InputElement>? lastShellFocus;

    /// <summary>
    /// A video started (full screen, the player takes the keyboard itself) or
    /// stopped: from full screen, the keyboard goes back where it was on the
    /// pages; from the mini player it stays where the browsing has it.
    /// </summary>
    private void VideoChanged()
    {
        if (watchedVideo is not null) watchedVideo.PropertyChanged -= OnVideoChanged;
        var wasMini = watchedVideo is not null && videoWasMini;
        watchedVideo = Model?.Video;
        videoWasMini = watchedVideo?.IsMini ?? false;
        if (watchedVideo is not null)
        {
            watchedVideo.PropertyChanged += OnVideoChanged;
            if (Model is { IsSidebarOpen: true } shell) shell.IsSidebarOpen = false;
        }
        else if (!wasMini) Dispatcher.UIThread.Post(BackFromVideo, DispatcherPriority.Loaded);
    }

    private void OnVideoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.IsMini) || watchedVideo is not { } video) return;
        videoWasMini = video.IsMini;
        // To the corner: the pages have the keyboard again, where it was.
        if (video.IsMini) Dispatcher.UIThread.Post(BackFromVideo, DispatcherPriority.Loaded);
    }

    private void BackFromVideo()
    {
        if (Model?.Video is { IsMini: false }) return;
        if (lastShellFocus?.TryGetTarget(out var was) == true && KeyFocus.CanFocus(was)) Land(was);
        else if (keyboardLast) FocusPage();
    }

    // ---- The Now Playing player ----

    /// <summary>Where the keyboard was as the player opened (a mini player's button), to go back to as it closes.</summary>
    private WeakReference<InputElement>? beforePlayer;

    /// <summary>
    /// The player opening takes the keyboard to its play/pause, rather than
    /// leaving it (and its highlight) on the button behind that opened it;
    /// closing brings it back there.
    /// </summary>
    private void OnNowPlayingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(NowPlayingViewModel.IsShown) || Model is not { } shell) return;
        if (shell.NowPlaying.IsShown)
        {
            beforePlayer = KeyFocus.Current(this) is InputElement was && !KeyFocus.IsWithin(was, NowPlayingPanel) ? new(was) : null;
            Dispatcher.UIThread.Post(() => { if (PlayerStart() is { } start) Land(start); }, DispatcherPriority.Loaded);
        }
        else
        {
            var back = beforePlayer;
            beforePlayer = null;
            // Put away by a tab chosen in the sidebar over it: the keyboard goes to that page, not back to a mini player.
            var forTab = shell.IsSidebarOpen;
            Dispatcher.UIThread.Post(() =>
            {
                if (forTab || Model is { IsSidebarOpen: true }) return;
                if (back?.TryGetTarget(out var was) == true && KeyFocus.CanFocus(was)) Land(was);
                else if (keyboardLast) FocusPage();
            }, DispatcherPriority.Loaded);
        }
    }

    /// <summary>The player's play/pause, the song's or the book's, whichever is showing.</summary>
    private InputElement? PlayerStart() =>
        NowPlayingPanel.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("main") && KeyFocus.CanFocus(b))
        ?? KeyFocus.FirstIn(NowPlayingPanel);

    /// <summary>Focuses <paramref name="element"/>, showing its highlight only if the keyboard (or a controller) brought it there.</summary>
    private void Land(InputElement element)
    {
        if (keyboardLast) KeyFocus.Move(element);
        else element.Focus();
    }

    private ShellViewModel? Model => DataContext as ShellViewModel;

    // The keys at the window, first: with nothing focused (the control the
    // keyboard was on went with its page) they'd not come through the shell.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        window = TopLevel.GetTopLevel(this);
        window?.AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
    }

    // Taken from the window the shell was in: as it leaves (for the video
    // player, say) it can no longer find it from itself, and the handler
    // stayed, one more each time it came back, all answering every key.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        window?.RemoveHandler(KeyDownEvent, OnKeyDownFirst);
        window = null;
        base.OnDetachedFromVisualTree(e);
    }

    private TopLevel? window;

    /// <summary>Keys pressed, so a page's search for where to start gives way once the keyboard's in use.</summary>
    private int keysPressed;

    private void Open()
    {
        if (Model is { } shell) shell.IsSidebarOpen = true;
    }

    private void Close()
    {
        if (Model is { } shell) shell.IsSidebarOpen = false;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Video)) VideoChanged();
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage();
        // Put away with the keyboard in it: back to the page.
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarOpen) && Model is { IsSidebarOpen: false } && HasFocusWithin(Sidebar)) ReturnToPage();
    }

    // ---- Keys ----

    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        keyboardLast = true;
        pointerResting = true;
        keysPressed++;
        if (Model is not { } shell || e.Handled) return;
        // A video: a controller's Y between full screen and the mini player;
        // full screen, every other key is the player's.
        if (shell.Video is { } video)
        {
            if (e.Key == Key.MediaMenu && video.CanMinimize)
            {
                shell.ToggleVideo();
                e.Handled = true;
                return;
            }
            if (!video.IsMini) return;
            // In the mini player: the play key (a controller's X) is the video's, not the paused song's.
            if (e.Key == Key.MediaPlayPause)
            {
                video.TogglePauseCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }
        var focused = KeyFocus.Current(this) as Visual;
        // In a menu or a dropdown's list (drawn in the window, so its keys pass
        // here first): its own, to move through and choose from.
        if (focused?.FindAncestorOfType<Avalonia.Controls.Primitives.OverlayPopupHost>(includeSelf: true) is not null) return;
        var inSidebar = focused is not null && KeyFocus.IsWithin(focused, Sidebar);
        var onPage = focused is not null && KeyFocus.IsWithin(focused, Page);
        var inTopBar = focused is not null && KeyFocus.IsWithin(focused, TopBar);
        var inPlayer = focused is not null && KeyFocus.IsWithin(focused, NowPlayingPanel);
        switch (e.Key)
        {
            // The volume keys (a controller's right stick) outside the video player: music's and
            // audiobooks' (the Sonos's, as a TV's remote turns it, or the player's). A trailer's are its own.
            case Key.VolumeUp or Key.VolumeDown when !TrailerView.IsShowing:
                shell.NowPlaying.ChangeVolume(e.Key == Key.VolumeUp ? 1 : -1);
                e.Handled = true;
                break;
            // The Now Playing player, over every page: the arrows stay in it
            // (they'd find the page under it too). A slider keeps its Left and
            // Right, to seek. Left with nothing further left slides the sidebar
            // out over the player, as over a page; Right or Back from the
            // sidebar comes back to it, a tab chosen there puts it away.
            // Play On open over the player: the arrows stay in it (a volume bar keeps Left and Right),
            // B closes it; Y opens it from anywhere in the player.
            case Key.Left or Key.Right or Key.Up or Key.Down when shell.NowPlaying.IsShown && shell.NowPlaying.IsChoosingSpeakers:
                if (NowPlayingPanel.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "SpeakerSheet") is { } sheet)
                {
                    if (focused is null || !KeyFocus.IsWithin(focused, sheet)) { if (KeyFocus.FirstIn(sheet) is { } first) KeyFocus.Move(first); }
                    else if (focused is Slider && e.Key is Key.Left or Key.Right) return;
                    else if (Neighbor(sheet, focused, e.Key) is { } near) KeyFocus.Move(near);
                }
                e.Handled = true;
                break;
            case Key.Escape or Key.BrowserBack when shell.NowPlaying.IsShown && shell.NowPlaying.IsChoosingSpeakers:
                shell.NowPlaying.CloseSpeakers();
                e.Handled = true;
                break;
            case Key.MediaMenu when shell.NowPlaying.IsShown && !shell.NowPlaying.IsChoosingSpeakers:
                shell.NowPlaying.OpenSpeakersCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when shell.NowPlaying.IsShown && !shell.IsSidebarOpen:
                if (!inPlayer) { if (PlayerStart() is { } start) KeyFocus.Move(start); }
                else if (focused is Slider && e.Key is Key.Left or Key.Right) return;
                else if (Neighbor(NowPlayingPanel, focused!, e.Key) is { } near) KeyFocus.Move(near);
                else if (e.Key == Key.Left) OpenWithKeyboard(focused as InputElement);
                e.Handled = true;
                break;
            // A on the tab's capsule: the sidebar, with the keyboard on the tab showing.
            case Key.Enter or Key.Space when focused == Capsule:
                OpenWithKeyboard(null);
                e.Handled = true;
                break;
            // The sidebar out (the pointer slid it) with the keyboard still behind it: the arrows start on its tab showing.
            case Key.Up or Key.Down or Key.Left when shell.IsSidebarOpen && !inSidebar:
                if (CurrentTabButton() is { } tab) KeyFocus.Move(tab);
                e.Handled = true;
                break;
            // Along the top row (the tab's capsule, the song or book playing):
            // Left past the capsule opens the sidebar; Down goes back to the page.
            case Key.Left or Key.Right when inTopBar:
                if (Neighbor(TopBar, focused!, e.Key) is { } along) KeyFocus.Move(along);
                else if (e.Key == Key.Left) OpenWithKeyboard(null);
                e.Handled = true;
                break;
            case Key.Down when inTopBar:
                ReturnToPage();
                e.Handled = true;
                break;
            case Key.Up when inTopBar:
                e.Handled = true;
                break;
            // The arrows with nothing focused (the page just changed, or the pointer was used): start on the page.
            case Key.Left or Key.Right or Key.Up or Key.Down when !inSidebar && !onPage && !shell.NowPlaying.IsShown:
                FocusPage();
                e.Handled = true;
                break;
            // Left with nothing further left (the key went unused, and the keyboard didn't move): the sidebar, on the tab showing.
            case Key.Left when onPage && focused is not TextBox && e.KeyModifiers == KeyModifiers.None:
                var from = focused;
                Dispatcher.UIThread.Post(() =>
                {
                    if (!e.Handled && KeyFocus.Current(this) == from && Model is { IsSidebarOpen: false }) OpenWithKeyboard(from as InputElement);
                }, DispatcherPriority.Input);
                break;
            // Up with nothing further up: the top row, on the song or book playing (else the capsule).
            case Key.Up when onPage && e.KeyModifiers == KeyModifiers.None:
                var below = focused;
                Dispatcher.UIThread.Post(() =>
                {
                    if (e.Handled || KeyFocus.Current(this) != below || Model is not { IsSidebarOpen: false } || !TopBar.IsEffectivelyVisible) return;
                    if (below is InputElement element) beforeSidebar = new WeakReference<InputElement>(element);
                    if (TopBarStart() is { } top) KeyFocus.Move(top);
                }, DispatcherPriority.Input);
                break;
            // Along the sidebar's rows (the audio bar's buttons side by side);
            // Right with nothing further right goes back to the page.
            case Key.Right when inSidebar:
                if (Neighbor(Sidebar, focused!, Key.Right) is { } right) KeyFocus.Move(right);
                else shell.IsSidebarOpen = false;
                e.Handled = true;
                break;
            // Nothing's left of the sidebar. Left there would move to the page
            // behind it, which put it away, and a held Left (a controller's
            // repeat) opened it again: in and out for as long as it was held.
            case Key.Left when inSidebar:
                if (Neighbor(Sidebar, focused!, Key.Left) is { } left) KeyFocus.Move(left);
                e.Handled = true;
                break;
            // Up and down the sidebar, kept to it: the window's own arrows
            // weigh the page behind it too, and could jump there. Up past
            // the first tab or down past the audio bar stays put.
            case Key.Up or Key.Down when inSidebar:
                if (Neighbor(Sidebar, focused!, e.Key) is { } next) KeyFocus.Move(next);
                e.Handled = true;
                break;
            // Back puts the sidebar away first (over the player, back to it), then the player.
            case Key.Escape or Key.BrowserBack when shell.IsSidebarOpen:
                shell.IsSidebarOpen = false;
                e.Handled = true;
                break;
            case Key.Escape or Key.BrowserBack when shell.NowPlaying.IsShown:
                break;
            // The remote's Menu (or a controller's B): back, or on a tab's own page, the sidebar.
            case Key.Escape or Key.BrowserBack when shell.CanGoBack:
                shell.Back();
                e.Handled = true;
                break;
            case Key.Escape or Key.BrowserBack:
                OpenWithKeyboard(focused as InputElement);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// The control next to <paramref name="from"/> in <paramref name="container"/>,
    /// the arrow's way. Up and down: the nearest whose middle is further that
    /// way, the most in line with it among those. Left and right: only along
    /// the same row. Null with nothing there.
    /// </summary>
    private static InputElement? Neighbor(Visual container, Visual from, Key direction)
    {
        if (from.TranslatePoint(new Point(from.Bounds.Width / 2, from.Bounds.Height / 2), container) is not { } middle) return null;
        var across = direction is Key.Left or Key.Right;
        InputElement? best = null;
        (double Ahead, double Beside) bestDistance = (double.MaxValue, double.MaxValue);
        foreach (var candidate in container.GetVisualDescendants().OfType<InputElement>())
        {
            if (candidate == from || !KeyFocus.CanFocus(candidate) || KeyFocus.IsWithin(candidate, from) || KeyFocus.IsWithin(from, candidate)) continue;
            if (candidate.TranslatePoint(new Point(candidate.Bounds.Width / 2, candidate.Bounds.Height / 2), container) is not { } at) continue;
            var ahead = direction switch
            {
                Key.Down => at.Y - middle.Y,
                Key.Up => middle.Y - at.Y,
                Key.Right => at.X - middle.X,
                _ => middle.X - at.X,
            };
            var beside = across ? Math.Abs(at.Y - middle.Y) : Math.Abs(at.X - middle.X);
            if (ahead < 1 || across && beside > from.Bounds.Height / 2) continue;
            var distance = (Math.Round(ahead), beside);
            if (distance.CompareTo(bestDistance) < 0) (best, bestDistance) = (candidate, distance);
        }
        return best;
    }

    /// <summary>Where Up from the page lands on the top row: the song or book playing, else the tab's capsule.</summary>
    private InputElement? TopBarStart() =>
        TopBar.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) == "Back to the Video" && KeyFocus.CanFocus(b))
        ?? TopBar.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => AutomationProperties.GetName(b) == "Now Playing" && KeyFocus.CanFocus(b))
        ?? (KeyFocus.CanFocus(Capsule) ? Capsule : null);

    /// <summary>Slides the sidebar out with the keyboard on the tab showing, remembering where it was on the page.</summary>
    private void OpenWithKeyboard(InputElement? from)
    {
        if (Model is not { } shell) return;
        if (from is not null && (KeyFocus.IsWithin(from, Page) || KeyFocus.IsWithin(from, NowPlayingPanel))) beforeSidebar = new WeakReference<InputElement>(from);
        shell.IsSidebarOpen = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (CurrentTabButton() is { } button) KeyFocus.Move(button);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>The sidebar's button for the tab showing.</summary>
    private Button? CurrentTabButton()
    {
        if (Model?.CurrentTab is not { } tab) return null;
        return TabList.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => ReferenceEquals(b.DataContext, tab));
    }

    /// <summary>Back to where the keyboard was on the page, or where the page starts.</summary>
    private void ReturnToPage()
    {
        if (beforeSidebar?.TryGetTarget(out var element) == true && KeyFocus.CanFocus(element)
            && (KeyFocus.IsWithin(element, Page) || KeyFocus.IsWithin(element, NowPlayingPanel)))
        {
            KeyFocus.Move(element);
            return;
        }
        FocusPage();
    }

    // ---- Pages ----

    /// <summary>
    /// A page came into view: the keyboard goes where it was on it, or where
    /// it starts. Pages build their rows as they load, so this tries for a
    /// few seconds: the first control stands in until the one the page
    /// prefers (a banner's Play, the programme airing) arrives.
    /// </summary>
    private void ShowPage()
    {
        beforeSidebar = null;
        var tries = 0;
        var page = Model?.CurrentPage;
        var keys = keysPressed;
        IInputElement? standIn = null;
        DispatcherTimer? retry = null;
        retry = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            tries++;
            var focused = KeyFocus.Current(this);
            // Gone, given up, or the keyboard has moved on its own. Or a key
            // was pressed: from then the keys say where focus goes. (It went
            // on putting the page's first control back while the keys opened
            // the sidebar, which that put away: in and out for three seconds.)
            if (Model?.CurrentPage != page || tries > 20 || keysPressed != keys || (HasFocusOnPage() && focused != standIn))
            {
                retry!.Stop();
                return;
            }
            if (Restored() is { } restored && restored != standIn)
            {
                Place(restored);
                retry!.Stop();
            }
            else if (KeyFocus.PreferredIn(Page) is { } preferred)
            {
                Place(preferred);
                retry!.Stop();
            }
            else if (!HasFocusOnPage() && KeyFocus.FirstIn(Page) is { } first)
            {
                Place(first);
                standIn = first;
            }
        });
        retry.Start();
    }

    private bool HasFocusOnPage() => KeyFocus.Current(this) is Visual focused && KeyFocus.IsWithin(focused, Page) && focused is not ContentControl;

    /// <summary>Where the keyboard was on this page when it was last left, if that's still there.</summary>
    private InputElement? Restored() =>
        Model?.CurrentPage is { } page && pageFocus.TryGetValue(page, out var saved) && saved.TryGetTarget(out var element)
            && element.IsEffectivelyVisible && KeyFocus.IsWithin(element, Page) && !KeyFocus.StartsFresh(Page) ? element : null;

    /// <summary>Focuses a control on the page: with its focus look for the keyboard's users, without it for the pointer's.</summary>
    private void Place(InputElement target)
    {
        if (keyboardLast) KeyFocus.Move(target);
        else target.Focus(NavigationMethod.Unspecified);
    }

    /// <summary>Focuses the page: where it was left, else its main action or first control.</summary>
    private void FocusPage()
    {
        if ((Restored() ?? KeyFocus.FirstIn(Page)) is { } target) Place(target);
    }

    /// <summary>The keyboard is on something in <paramref name="container"/>.</summary>
    private bool HasFocusWithin(Control container) =>
        KeyFocus.Current(this) is Visual focused && KeyFocus.IsWithin(focused, container);

}
