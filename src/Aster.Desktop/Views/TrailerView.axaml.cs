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
/// Linux, so it's a borderless one, unfocused, laid over the page's Host and
/// kept there as the window moves or resizes. The keyboard (and so the
/// controller) stays on the page: A, X, Left and Right become the player's
/// commands, and B goes back, which closes it.
/// </summary>
public partial class TrailerView : UserControl
{
    private NativeWebDialog? player;
    private TopLevel? top;

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
        player.AdapterCreated += (_, _) => Undecorate();
        player.NavigateToString(trailer.Html, TrailerViewModel.Origin);
        player.Show(top);
        Dispatcher.UIThread.Post(() =>
        {
            Place();
            BackButton.Focus(NavigationMethod.Directional);
        }, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (top is Window owner) owner.PositionChanged -= OnMoved;
        var closing = player;
        player = null;
        if (closing is not null)
        {
            closing.Close();
            closing.Dispose();
        }
        base.OnDetachedFromVisualTree(e);
    }

    private void OnMoved(object? sender, PixelPointEventArgs e) => Place();

    /// <summary>The player's window over the Host, in screen pixels.</summary>
    private void Place()
    {
        if (player is null || top is null || Host.Bounds.Width <= 0) return;
        var corner = Host.PointToScreen(new Point(0, 0));
        var scale = top.RenderScaling;
        player.Move(corner.X, corner.Y);
        player.Resize((int)Math.Round(Host.Bounds.Width * scale), (int)Math.Round(Host.Bounds.Height * scale));
    }

    /// <summary>No title bar or frame: it's the page's player, not a window to manage.</summary>
    private void Undecorate()
    {
        if (player?.TryGetPlatformHandle() is not { Handle: var handle } || handle == IntPtr.Zero) return;
        try { gtk_window_set_decorated(handle, false); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("libgtk-3.so.0")]
    private static extern void gtk_window_set_decorated(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool decorated);

    private void OnKey(object? sender, KeyEventArgs e)
    {
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

    private static async System.Threading.Tasks.Task Run(NativeWebDialog on, string script)
    {
        try { await on.InvokeScript(script); }
        catch (Exception error) when (error is InvalidOperationException or NotSupportedException or ObjectDisposedException) { }
    }
}
