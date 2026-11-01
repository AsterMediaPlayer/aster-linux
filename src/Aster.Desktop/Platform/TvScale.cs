using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Aster.Desktop.Platform;

/// <summary>
/// Aster is a couch app: its pages are laid out for a screen about 1280
/// units wide, as the Apple TV app's are for 1920 points at a distance, so
/// type and pictures read from across the room. On Linux (X11, XWayland,
/// gamescope) the window's scale is set to the primary monitor's width over
/// 1280 before Avalonia starts: 1.5 on a 1080p TV, 3 on 4K, 1 in a 1280×720
/// gamescope. Video still draws at the screen's full resolution, as the
/// scale is the window's own. ASTER_SCALE=1.25 (or any number) chooses it;
/// a scale already given Avalonia (AVALONIA_GLOBAL_SCALE_FACTOR or
/// AVALONIA_SCREEN_SCALE_FACTORS) is left as it is.
/// </summary>
public static partial class TvScale
{
    /// <summary>The width, in units, the pages are laid out for.</summary>
    public const double LayoutWidth = 1280;

    public static void Apply()
    {
        if (!OperatingSystem.IsLinux()) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")))
            return;
        double scale;
        if (double.TryParse(Environment.GetEnvironmentVariable("ASTER_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var chosen) && chosen is > 0.5 and < 8)
            scale = chosen;
        else if (PrimaryWidth() is { } width)
            // To the nearest quarter, never smaller than the desktop's own size.
            scale = Math.Max(1, Math.Round(width / LayoutWidth * 4) / 4);
        else
            return;
        Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", scale.ToString(CultureInfo.InvariantCulture));
        System.Diagnostics.Trace.WriteLine($"TV scale: {scale}");
    }

    /// <summary>The primary monitor's width in pixels (XRandR's monitors), else the first's; null without X.</summary>
    private static int? PrimaryWidth()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return null;
            try
            {
                var monitors = XRRGetMonitors(display, XDefaultRootWindow(display), true, out var count);
                if (monitors == IntPtr.Zero || count <= 0) return XDisplayWidth(display, XDefaultScreen(display));
                try
                {
                    int? first = null;
                    var size = Marshal.SizeOf<MonitorInfo>();
                    for (var i = 0; i < count; i++)
                    {
                        var monitor = Marshal.PtrToStructure<MonitorInfo>(monitors + i * size);
                        first ??= monitor.Width;
                        if (monitor.Primary != 0) return monitor.Width;
                    }
                    return first;
                }
                finally { XRRFreeMonitors(monitors); }
            }
            finally { XCloseDisplay(display); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public IntPtr Name;
        public int Primary;
        public int Automatic;
        public int OutputCount;
        public int X, Y, Width, Height, WidthMm, HeightMm;
        public IntPtr Outputs;
    }

    [LibraryImport("libX11.so.6")] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport("libX11.so.6")] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport("libX11.so.6")] private static partial IntPtr XDefaultRootWindow(IntPtr display);
    [LibraryImport("libX11.so.6")] private static partial int XDefaultScreen(IntPtr display);
    [LibraryImport("libX11.so.6")] private static partial int XDisplayWidth(IntPtr display, int screen);
    [LibraryImport("libXrandr.so.2")] private static partial IntPtr XRRGetMonitors(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool active, out int count);
    [LibraryImport("libXrandr.so.2")] private static partial void XRRFreeMonitors(IntPtr monitors);
}
