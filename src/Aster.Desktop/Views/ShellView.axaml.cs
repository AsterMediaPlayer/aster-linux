using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

        DataContextChanged += (_, _) =>
        {
            if (watched is not null) watched.PropertyChanged -= OnShellChanged;
            watched = Model;
            if (watched is not null) watched.PropertyChanged += OnShellChanged;
            ShowPage();
        };
    }

    private ShellViewModel? Model => DataContext as ShellViewModel;

    // The keys at the window, first: with nothing focused (the control the
    // keyboard was on went with its page) they'd not come through the shell.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TopLevel.GetTopLevel(this)?.AddHandler(KeyDownEvent, OnKeyDownFirst, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        TopLevel.GetTopLevel(this)?.RemoveHandler(KeyDownEvent, OnKeyDownFirst);
        base.OnDetachedFromVisualTree(e);
    }

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
        if (e.PropertyName == nameof(ShellViewModel.CurrentPage)) ShowPage();
        // Put away with the keyboard in it: back to the page.
        if (e.PropertyName == nameof(ShellViewModel.IsSidebarOpen) && Model is { IsSidebarOpen: false } && HasFocusWithin(Sidebar)) ReturnToPage();
    }

    // ---- Keys ----

    private void OnKeyDownFirst(object? sender, KeyEventArgs e)
    {
        keyboardLast = true;
        pointerResting = true;
        if (Model is not { } shell || e.Handled) return;
        var focused = KeyFocus.Current(this) as Visual;
        var inSidebar = focused is not null && KeyFocus.IsWithin(focused, Sidebar);
        var onPage = focused is not null && KeyFocus.IsWithin(focused, Page);
        switch (e.Key)
        {
            // The sidebar out (the pointer slid it) with the keyboard still behind it: the arrows start on its tab showing.
            case Key.Up or Key.Down or Key.Left when shell.IsSidebarOpen && !inSidebar && !shell.NowPlaying.IsShown:
                if (CurrentTabButton() is { } tab) KeyFocus.Move(tab);
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
            // Right from the sidebar, back to the page.
            case Key.Right when inSidebar:
                shell.IsSidebarOpen = false;
                e.Handled = true;
                break;
            // Nothing's left of the sidebar. Left there would move to the page
            // behind it, which put it away, and a held Left (a controller's
            // repeat) opened it again: in and out for as long as it was held.
            case Key.Left when inSidebar:
                e.Handled = true;
                break;
            // Up and down the sidebar, kept to it: the window's own arrows
            // weigh the page behind it too, and could jump there. Up past
            // the first tab or down past Exit stays put.
            case Key.Up or Key.Down when inSidebar:
                if (NextInSidebar(focused!, e.Key == Key.Down) is { } next) KeyFocus.Move(next);
                e.Handled = true;
                break;
            case Key.Escape or Key.BrowserBack when shell.NowPlaying.IsShown:
                break;
            case Key.Escape or Key.BrowserBack when shell.IsSidebarOpen:
                shell.IsSidebarOpen = false;
                e.Handled = true;
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
    /// The sidebar's next control below (or above) <paramref name="from"/>:
    /// the nearest whose middle is further that way, the most in line with it
    /// among those (the audio bar's buttons sit side by side). Null at the end.
    /// </summary>
    private InputElement? NextInSidebar(Visual from, bool down)
    {
        if (from.TranslatePoint(new Point(from.Bounds.Width / 2, from.Bounds.Height / 2), Sidebar) is not { } middle) return null;
        InputElement? best = null;
        (double Ahead, double Beside) bestDistance = (double.MaxValue, double.MaxValue);
        foreach (var candidate in Sidebar.GetVisualDescendants().OfType<InputElement>())
        {
            if (candidate == from || !KeyFocus.CanFocus(candidate) || KeyFocus.IsWithin(candidate, from) || KeyFocus.IsWithin(from, candidate)) continue;
            if (candidate.TranslatePoint(new Point(candidate.Bounds.Width / 2, candidate.Bounds.Height / 2), Sidebar) is not { } at) continue;
            var ahead = down ? at.Y - middle.Y : middle.Y - at.Y;
            if (ahead < 1) continue;
            var distance = (Math.Round(ahead), Math.Abs(at.X - middle.X));
            if (distance.CompareTo(bestDistance) < 0) (best, bestDistance) = (candidate, distance);
        }
        return best;
    }

    /// <summary>Slides the sidebar out with the keyboard on the tab showing, remembering where it was on the page.</summary>
    private void OpenWithKeyboard(InputElement? from)
    {
        if (Model is not { } shell) return;
        if (from is not null && KeyFocus.IsWithin(from, Page)) beforeSidebar = new WeakReference<InputElement>(from);
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
        if (beforeSidebar?.TryGetTarget(out var element) == true && element.IsEffectivelyVisible && KeyFocus.IsWithin(element, Page))
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
        IInputElement? standIn = null;
        DispatcherTimer? retry = null;
        retry = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            tries++;
            var focused = KeyFocus.Current(this);
            // Gone, given up, or the keyboard has moved on its own.
            if (Model?.CurrentPage != page || tries > 20 || (HasFocusOnPage() && focused != standIn))
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
            && element.IsEffectivelyVisible && KeyFocus.IsWithin(element, Page) ? element : null;

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
