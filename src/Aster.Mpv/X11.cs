using System.Runtime.InteropServices;

namespace Aster.Mpv;

/// <summary>
/// A connection to the X server, for mpv's VA-API to make its display from:
/// opened once, the first time video is drawn on Linux under X11 (XWayland
/// included), and kept for the life of the app. Zero elsewhere, or if
/// libX11 can't be loaded.
/// </summary>
internal static partial class X11
{
    private static readonly Lazy<IntPtr> display = new(Open);

    public static IntPtr Display => display.Value;

    private static IntPtr Open()
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return IntPtr.Zero;
        try { return XOpenDisplay(IntPtr.Zero); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return IntPtr.Zero; }
    }

    [LibraryImport("libX11.so.6")]
    private static partial IntPtr XOpenDisplay(IntPtr name);
}
