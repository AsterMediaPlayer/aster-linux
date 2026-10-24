using Avalonia.Controls;
using Avalonia.Threading;

namespace Aster.Desktop.Controls;

/// <summary>
/// The window taken full screen by the views that want it (the player, a
/// disc menu), and given back as the last of them leaves. Shared, as one
/// hands over to the next (the menu to the player, the player back) with
/// the window full screen throughout.
/// </summary>
public static class FullScreen
{
    /// <summary>How the window was before it went full screen.</summary>
    private static WindowState before = WindowState.Normal;

    /// <summary>The views in the window that hold it full screen.</summary>
    private static int holders;

    /// <summary>A view that wants full screen arrived; with <paramref name="enter"/>, the window goes full screen now.</summary>
    public static void Hold(Window? window, bool enter)
    {
        holders++;
        if (enter && window is { WindowState: not WindowState.FullScreen }) Toggle(window);
    }

    /// <summary>
    /// The view left. Once the page behind is back in the window, not
    /// partway through swapping it in, and only when no other view has taken
    /// over, the window comes back as it was.
    /// </summary>
    public static void Release(Window? window)
    {
        holders--;
        if (window is { WindowState: WindowState.FullScreen }) Dispatcher.UIThread.Post(() => { if (holders == 0) window.WindowState = before; });
    }

    public static void Toggle(Window window)
    {
        if (window.WindowState == WindowState.FullScreen)
        {
            window.WindowState = before;
        }
        else
        {
            before = window.WindowState;
            window.WindowState = WindowState.FullScreen;
        }
    }
}
