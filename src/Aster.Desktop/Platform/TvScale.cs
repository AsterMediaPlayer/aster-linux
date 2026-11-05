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
        var (width, desktop) = Screen();
        double scale;
        if (double.TryParse(Environment.GetEnvironmentVariable("ASTER_SCALE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var chosen) && chosen is > 0.5 and < 8)
            scale = chosen;
        else if (width is { } pixels)
            scale = Math.Max(1, pixels / LayoutWidth);
        else
            return;
        // Avalonia multiplies its global factor by the desktop's own scale
        // (Xft.dpi over 96: 1.25 on a KDE set to 125%), so the factor is what's
        // left of the scale wanted: 1.5 / 1.25 = 1.2. (Given as the factor
        // alone, it came to 1.875, the pages 1024 units across.)
        var factor = scale / desktop;
        Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", factor.ToString("0.####", CultureInfo.InvariantCulture));
        System.Diagnostics.Trace.WriteLine($"TV scale: {scale} (the desktop's {desktop}, so a factor of {factor:0.###})");
    }

    /// <summary>
    /// The primary monitor's width in pixels (XRandR's monitors, else the
    /// first's), and the desktop's scale as Avalonia reads it (Xft.dpi over
    /// 96; 1 without one); no width without X.
    /// </summary>
    private static (int? Width, double Desktop) Screen()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return (null, 1);
        try
        {
            var display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) return (null, 1);
            try
            {
                var desktop = 1.0;
                var dpi = XGetDefault(display, "Xft", "dpi");
                if (dpi != IntPtr.Zero && double.TryParse(Marshal.PtrToStringUTF8(dpi), NumberStyles.Float, CultureInfo.InvariantCulture, out var dots) && dots > 0)
                    desktop = dots / 96;
                return (Width(display), desktop);
            }
            finally { XCloseDisplay(display); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException) { return (null, 1); }
    }

    private static int? Width(IntPtr display)
    {
        {
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
        }
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
    [LibraryImport("libX11.so.6", StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr XGetDefault(IntPtr display, string program, string option);
    [LibraryImport("libXrandr.so.2")] private static partial IntPtr XRRGetMonitors(IntPtr display, IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool active, out int count);
    [LibraryImport("libXrandr.so.2")] private static partial void XRRFreeMonitors(IntPtr monitors);
}
