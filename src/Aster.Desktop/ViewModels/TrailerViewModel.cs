using System;
using System.Linq;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// A trailer from YouTube, played in the app in YouTube's own embedded
/// player (its privacy-enhanced youtube-nocookie.com one), as YouTube's
/// terms allow, rather than in a browser: a port of the Apple app's
/// TrailerPlayer. The controller drives it through YouTube's player API:
/// A or X pauses and plays, Left and Right skip ten seconds, B goes back.
/// </summary>
public sealed partial class TrailerViewModel(INavigator navigator, string youTubeId, string title) : ViewModelBase
{
    public string Title => title;

    /// <summary>YouTube wants to know which site embeds it (it refuses to play without), so the page is given Aster's own.</summary>
    public static Uri Origin { get; } = new("https://astermediaplayer.com");

    /// <summary>The page holding the player, starting at once, and the commands the controller sends it.</summary>
    public string Html
    {
        get
        {
            var id = Uri.EscapeDataString(youTubeId);
            return $$"""
                <!DOCTYPE html><html><head>
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <style>html,body{margin:0;height:100%;background:#000;overflow:hidden}iframe{position:absolute;inset:0;width:100%;height:100%;border:0}</style>
                </head><body>
                <iframe id="player" src="https://www.youtube-nocookie.com/embed/{{id}}?autoplay=1&playsinline=1&rel=0&modestbranding=1&enablejsapi=1&origin={{Origin.GetLeftPart(UriPartial.Authority)}}"
                        allow="autoplay; encrypted-media; picture-in-picture; fullscreen" allowfullscreen
                        referrerpolicy="strict-origin-when-cross-origin"></iframe>
                <script>
                var state = -1, time = 0, started = false, player = document.getElementById('player');
                function send(func, args) { player.contentWindow.postMessage(JSON.stringify({event: 'command', func: func, args: args || []}), '*'); }
                player.addEventListener('load', function () { player.contentWindow.postMessage(JSON.stringify({event: 'listening'}), '*'); });
                window.addEventListener('message', function (e) {
                  try {
                    var data = typeof e.data === 'string' ? JSON.parse(e.data) : e.data;
                    var info = data && data.info;
                    // Ready, Aster's told, to play it: WebKit plays it with its sound only for a
                    // click, or for a script Aster runs, not for autoplay or the page's own.
                    if (data && data.event === 'onReady' && !started) { started = true; if (typeof invokeCSharpAction === 'function') invokeCSharpAction('ready'); }
                    if (info && typeof info.playerState === 'number') state = info.playerState;
                    if (info && typeof info.currentTime === 'number') time = info.currentTime;
                  } catch (_) {}
                });
                function asterPlay() { send('playVideo'); }
                function asterToggle() { send(state === 1 ? 'pauseVideo' : 'playVideo'); }
                function asterSkip(seconds) { time = Math.max(0, time + seconds); send('seekTo', [time, true]); }
                // Full screen, the player's window has the keyboard: its keys, as the page's.
                document.addEventListener('keydown', function (e) {
                  if (e.key === 'Escape' || e.key === 'Backspace' || e.key === 'BrowserBack' || (e.altKey && e.key === 'ArrowLeft')) {
                    if (typeof invokeCSharpAction === 'function') invokeCSharpAction('back');
                  }
                  else if (e.key === 'Enter' || e.key === ' ' || e.key === 'k' || e.key === 'MediaPlayPause') asterToggle();
                  else if (e.key === 'ArrowLeft' || e.key === 'j') asterSkip(-10);
                  else if (e.key === 'ArrowRight' || e.key === 'l') asterSkip(10);
                  else return;
                  e.preventDefault();
                });
                </script>
                </body></html>
                """;
        }
    }

    /// <summary>
    /// Whether WebKitGTK is here to play it in, with GStreamer's good plugins
    /// (YouTube's MP4 and WebM, and the sound's output: without them WebKit's
    /// player crashes, and shows black). Without either, the trailer opens
    /// in the browser.
    /// </summary>
    public static bool CanPlay => Missing is null;

    /// <summary>What trailers need that isn't installed, to say so; null when they can play.</summary>
    public static string? Missing { get; } = !OperatingSystem.IsLinux() ? null
        : !NativeLibrary.TryLoad("libwebkit2gtk-4.1.so.0", out _) ? "Trailers play with WebKitGTK (webkit2gtk-4.1), which isn't installed; opening it in the browser."
        : !HasGoodPlugins() ? "Trailers need GStreamer's good plugins (gst-plugins-good), which aren't installed; opening it in the browser."
        : null;

    private static bool HasGoodPlugins()
    {
        var folders = (Environment.GetEnvironmentVariable("GST_PLUGIN_PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/usr/lib/gstreamer-1.0", "/usr/lib64/gstreamer-1.0", "/usr/lib/x86_64-linux-gnu/gstreamer-1.0", "/usr/lib/aarch64-linux-gnu/gstreamer-1.0"]);
        bool Has(string plugin) => folders.Any(folder => System.IO.File.Exists(System.IO.Path.Combine(folder, plugin)));
        var good = Has("libgstautodetect.so") && Has("libgstisomp4.so");
        if (!good) System.Diagnostics.Trace.WriteLine("Trailers: GStreamer's good plugins (gst-plugins-good) aren't installed, so they open in the browser");
        return good;
    }

    [RelayCommand]
    private void Back() => navigator.Back();
}
