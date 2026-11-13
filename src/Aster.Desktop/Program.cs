using Avalonia;
using System;

namespace Aster.Desktop;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // The TV home screen's Continue Watching row: printed, no window, and no
        // debug log (whose rotation would push out the app's own).
        if (Array.IndexOf(args, "--home-screen-row") >= 0)
        {
            Console.Out.Write(Aster.Core.Integration.HomeScreenRow.JsonAsync().GetAwaiter().GetResult());
            return;
        }
        if (Array.IndexOf(args, "--bigscreen-install") >= 0 || Array.IndexOf(args, "--bigscreen-uninstall") >= 0)
        {
            var restart = Array.IndexOf(args, "--no-restart") < 0;
            if (Array.IndexOf(args, "--bigscreen-uninstall") >= 0) Aster.Core.Integration.BigscreenInstall.Uninstall(restart);
            else if (Aster.Core.Integration.BigscreenInstall.Install(restart) is { } why) { Console.Error.WriteLine(why); Environment.Exit(1); }
            Console.Out.WriteLine(Aster.Core.Integration.BigscreenInstall.State);
            return;
        }
        // A debug report's log and the last crash: before anything else can fail.
        Aster.Core.Diagnostics.DebugLog.Install();
        // An aster:// link with Aster already running: handed to it, and this launch goes.
        if (Aster.Core.DeepLink.Find(args) is { } link && Aster.Desktop.Platform.DeepLinks.HandOff(link)) return;
        // From the TV home screen: the Aster already open plays it, and this one's done.
        if (Array.IndexOf(args, "--play") >= 0 && Aster.Desktop.Platform.InstanceChannel.TrySend(args)) return;
        // ASTER_LOG_AVALONIA: Avalonia's own messages (which renderer it chose, and why) on the console.
        if (Environment.GetEnvironmentVariable("ASTER_LOG_AVALONIA") is not null)
        {
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
            System.Diagnostics.Trace.AutoFlush = true;
        }
        // A couch app: laid out for about 1280 units across, whatever the screen.
        Aster.Desktop.Platform.TvScale.Apply();
        // Trailers play in a WebKitGTK window laid over the page: GTK on the
        // same X server as Avalonia (XWayland, gamescope), where it can be
        // placed, not on Wayland beside it.
        if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GDK_BACKEND")))
            Environment.SetEnvironmentVariable("GDK_BACKEND", "x11");
#if WINDOWS
        // Before any window, which takes the process's app id.
        WindowsAppIdentity.Apply();
#endif
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .With(LinuxOptions())
            .LogToTrace(Environment.GetEnvironmentVariable("ASTER_LOG_AVALONIA") is null ? Avalonia.Logging.LogEventLevel.Warning : Avalonia.Logging.LogEventLevel.Information);

    /// <summary>
    /// Video is drawn with OpenGL, so a renderer Avalonia turns down means no
    /// video at all. Its list also turns down VMware's and VirtualBox's
    /// SVGA3D, a virtual machine's only OpenGL, which plays well enough; this
    /// keeps only Mesa's software renderer (llvmpipe) out, which a PC with a
    /// graphics driver never needs. ASTER_SOFTWARE_GL=1 allows that too, for
    /// a virtual machine without 3D acceleration, slow as it is.
    ///
    /// EGL first: through it mpv puts VA-API's frames straight into OpenGL,
    /// where under GLX it copies each one back from the GPU and up again, too
    /// slow for a 4K film. GLX where EGL won't start; ASTER_GLX=1 forces it.
    ///
    /// Menus drawn inside the window, not as windows of their own: under
    /// gamescope a menu's own window takes the focus from the app's, and
    /// the menu shuts the moment it opens.
    /// </summary>
    private static X11PlatformOptions LinuxOptions() => new()
    {
        OverlayPopups = true,
        RenderingMode = Environment.GetEnvironmentVariable("ASTER_GLX") is null
            ? [X11RenderingMode.Egl, X11RenderingMode.Glx, X11RenderingMode.Software]
            : [X11RenderingMode.Glx, X11RenderingMode.Software],
        GlxRendererBlacklist = Environment.GetEnvironmentVariable("ASTER_SOFTWARE_GL") is null ? ["llvmpipe"] : [],
    };
}
