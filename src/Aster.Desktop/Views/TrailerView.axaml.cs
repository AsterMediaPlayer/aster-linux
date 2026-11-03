using System;
using System.Runtime.InteropServices;
using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace Aster.Desktop.Views;

/// <summary>
/// The trailer's player: WebKitGTK only plays in a window of its own on
/// Linux, so it's a borderless one, full screen (or, before it's shown, laid
/// over the page's Host and kept there as the window moves or resizes).
/// The controller stays on the page: A, X, Left and Right become the
/// player's commands, and B goes back, which closes it.
/// </summary>
public partial class TrailerView : UserControl
{
    private NativeWebDialog? player;
    private TopLevel? top;
    private bool fullScreen;

    /// <summary>Whether a trailer's player is up: its window takes the focus, but the controller still drives the page.</summary>
    public static bool IsShowing { get; private set; }

    /// <summary>The trailer up now, to tell when it's closed (its page's DataContext may be gone by then).</summary>
    private TrailerViewModel? showing;

    public TrailerView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
        Host.LayoutUpdated += (_, _) => Place();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is not TrailerViewModel trailer || TopLevel.GetTopLevel(this) is not { } window) return;
        top = window;
        if (top is Window owner) owner.PositionChanged += OnMoved;
        player = new NativeWebDialog { Title = trailer.Title, CanUserResize = false, ShowFocused = false, DefaultBackground = Avalonia.Media.Colors.Black };
        // Closed some other way (the window manager): the page goes too.
        player.Closing += (_, _) => Dispatcher.UIThread.Post(() => { if (player is not null && DataContext is TrailerViewModel open) open.BackCommand.Execute(null); });
        // From the page: its player's ready, to be played (run by Aster, so with its sound);
        // or the keyboard's way back, should the player's window have it.
        player.WebMessageReceived += (_, message) => Dispatcher.UIThread.Post(() =>
        {
            if (player is null) return;
            if (message.Body == "ready") _ = Run(player, "asterPlay()");
            else if (message.Body == "back" && DataContext is TrailerViewModel open) open.BackCommand.Execute(null);
        });
        // Again once it's loaded, should it have been shown after the first; and the focus
        // given back to the page (over the page rather than full screen, it takes it).
        player.NavigationCompleted += (_, _) => Dispatcher.UIThread.Post(() => { FillScreen(); Reclaim(); });
        player.NavigateToString(trailer.Html, TrailerViewModel.Origin);
        IsShowing = true;
        showing = trailer;
        trailer.Opened();
        player.Show(top);
        Dispatcher.UIThread.Post(() =>
        {
            FillScreen();
            Place();
            Reclaim();
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (top is Window owner) owner.PositionChanged -= OnMoved;
        var closing = player;
        player = null;
        IsShowing = false;
        showing?.Closed();
        showing = null;
        if (closing is not null)
        {
            closing.Close();
            closing.Dispose();
        }
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// The focus on the page's Back button, where the controller's buttons
    /// land. Over the page, the page's window is given the keyboard back too;
    /// full screen, the player keeps it (raising the page would hide the
    /// player), and its page hears the keys itself.
    /// </summary>
    private void Reclaim()
    {
        if (player is null) return;
        if (!fullScreen && top is Window window) window.Activate();
        BackButton.Focus(NavigationMethod.Directional);
    }

    private void OnMoved(object? sender, PixelPointEventArgs e) => Place();

    /// <summary>The player's window over the Host, in screen pixels; full screen, it stays as it is.</summary>
    private void Place()
    {
        if (player is null || top is null || fullScreen || Host.Bounds.Width <= 0) return;
        var corner = Host.PointToScreen(new Point(0, 0));
        var scale = top.RenderScaling;
        player.Move(corner.X, corner.Y);
        player.Resize((int)Math.Round(Host.Bounds.Width * scale), (int)Math.Round(Host.Bounds.Height * scale));
    }

    /// <summary>
    /// The player full screen, on the page's screen, with no title bar or
    /// frame: it's the page's player, not a window to manage. Once it's shown:
    /// showing it, the dialog frames it again, and makes it a toolbar.
    /// </summary>
    private void FillScreen()
    {
        if (player?.TryGetPlatformHandle() is not { Handle: var handle } || handle == IntPtr.Zero) return;
        if (top?.Screens?.ScreenFromTopLevel(top) is { } screen)
        {
            // Onto the page's screen first, so it fills that one.
            player.Move(screen.Bounds.X, screen.Bounds.Y);
            player.Resize(screen.Bounds.Width, screen.Bounds.Height);
        }
        fullScreen = true;
        // GTK's calls only on GTK's own thread: from Avalonia's, they hang it, or go unheard.
        _ = Avalonia.X11.Interop.GtkInteropHelper.RunOnGlibThread(() =>
        {
            try
            {
                // A window of its own (not the toolbar it's shown as, which the window manager won't
                // fill the screen with), unframed: on its GDK window, as it's already shown.
                if (gtk_widget_get_window(handle) is var window and not 0)
                {
                    gdk_window_set_type_hint(window, 0);
                    gdk_window_set_decorations(window, 0);
                }
                gtk_window_set_decorated(handle, false);
                gtk_window_fullscreen(handle);
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
            return true;
        });
    }

    [DllImport("libgtk-3.so.0")]
    private static extern void gtk_window_set_decorated(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool decorated);

    [DllImport("libgtk-3.so.0")]
    private static extern void gtk_window_fullscreen(IntPtr window);

    [DllImport("libgtk-3.so.0")]
    private static extern IntPtr gtk_widget_get_window(IntPtr widget);

    [DllImport("libgdk-3.so.0")]
    private static extern void gdk_window_set_type_hint(IntPtr window, int hint);

    [DllImport("libgdk-3.so.0")]
    private static extern void gdk_window_set_decorations(IntPtr window, int decorations);

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.VolumeUp or Key.VolumeDown && player is not null)
        {
            e.Handled = true;
            _ = ChangeVolumeAsync(player, e.Key == Key.VolumeUp ? 1 : -1);
            return;
        }
        var script = e.Key switch
        {
            Key.Enter or Key.Space or Key.MediaPlayPause => "asterToggle()",
            Key.Left or Key.J => "asterSkip(-10)",
            Key.Right or Key.L => "asterSkip(10)",
            _ => null,
        };
        if (script is null || player is null) return;
        e.Handled = true;
        _ = Run(player, script);
    }

    /// <summary>
    /// The volume keys: the confirmed Sonos's own, as a TV's remote turns it,
    /// else YouTube's player's; shown on the player's page, which is over
    /// everything of Aster's.
    /// </summary>
    private static async System.Threading.Tasks.Task ChangeVolumeAsync(NativeWebDialog on, int direction)
    {
        if (await Aster.Desktop.Audio.SoundbarVolume.ChangeAsync(direction * 2) is var (room, level))
            await Run(on, $"asterNotice({System.Text.Json.JsonSerializer.Serialize($"{room} volume {level}")})");
        else await Run(on, $"asterVolume({direction * 10})");
    }

    private static async System.Threading.Tasks.Task Run(NativeWebDialog on, string script)
    {
        try { await on.InvokeScript(script); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
    }
}
