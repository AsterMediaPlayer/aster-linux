using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Aster.Desktop.ViewModels;
using Aster.Desktop.Views;

namespace Aster.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
#if DEBUG
            // ASTER_LOG names a file for the app's and mpv's messages.
            if (System.Environment.GetEnvironmentVariable("ASTER_LOG") is { } log)
            {
                System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(log));
                System.Diagnostics.Trace.AutoFlush = true;
            }
#endif
            var args = desktop.Args ?? [];
            var play = System.Array.IndexOf(args, "--play") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            var bitrate = System.Array.IndexOf(args, "--max-bitrate") is var b and >= 0 && b + 1 < args.Length && int.TryParse(args[b + 1], out var cap) ? cap : (int?)null;
            var start = System.Array.IndexOf(args, "--start") is var s and >= 0 && s + 1 < args.Length
                && double.TryParse(args[s + 1], System.Globalization.CultureInfo.InvariantCulture, out var seconds) ? seconds : (double?)null;
            var main = new MainViewModel(play, bitrate, start);
            desktop.MainWindow = new MainWindow { DataContext = main };
            desktop.ShutdownRequested += (_, _) => main.Shutdown();
            System.Threading.Tasks.Task.Run(() => Aster.Core.Jellyfin.ImageCache.Trim());
        }

        base.OnFrameworkInitializationCompleted();
    }
}