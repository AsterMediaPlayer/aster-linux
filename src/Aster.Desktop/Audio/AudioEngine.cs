using System;
using System.Collections.Generic;
using System.Globalization;
using Aster.Core.Jellyfin;
using Aster.Mpv;
using Avalonia.Threading;

namespace Aster.Desktop.Audio;

/// <summary>
/// An mpv with no picture, for songs and books. Its events come back on
/// the UI thread. Made the first time something plays.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private MpvPlayer? mpv;

    /// <summary>A file opened and is ready.</summary>
    public event Action? Loaded;

    /// <summary>The file played to its end.</summary>
    public event Action? Ended;

    /// <summary>The file couldn't be played; mpv's reason.</summary>
    public event Action<string>? Failed;

    private MpvPlayer Mpv
    {
        get
        {
            if (mpv is not null) return mpv;
            mpv = MpvPlayer.Create(new Dictionary<string, string>
            {
                ["video"] = "no",
                ["vo"] = "null",
                ["audio-display"] = "no",
                ["idle"] = "yes",
                ["input-default-bindings"] = "no",
                ["terminal"] = "no",
                ["ytdl"] = "no",
                ["user-agent"] = $"{JellyfinDevice.ClientName}/{JellyfinDevice.Version}",
#if DEBUG
                // ASTER_MUTE: silent, for trying the app without disturbing anyone.
                ["mute"] = Environment.GetEnvironmentVariable("ASTER_MUTE") is null ? "no" : "yes",
#endif
            });
            mpv.FileLoaded += () => Dispatcher.UIThread.Post(() => Loaded?.Invoke());
            mpv.EndFile += end =>
            {
                // A file replaced by the next one ends with Stop; that's not news.
                if (end.Reason == MpvEndFileReason.Eof) Dispatcher.UIThread.Post(() => Ended?.Invoke());
                else if (end.Reason == MpvEndFileReason.Error) Dispatcher.UIThread.Post(() => Failed?.Invoke(end.ErrorMessage ?? "This can't be played."));
            };
            return mpv;
        }
    }

    /// <summary>Opens <paramref name="url"/> at <paramref name="start"/> seconds, playing or paused there.</summary>
    public void Load(Uri url, double start, bool playing)
    {
        try
        {
            Mpv.SetProperty("pause", !playing);
            var options = start > 0 ? "start=" + start.ToString("0.###", CultureInfo.InvariantCulture) : "";
            _ = Mpv.CommandAsync("loadfile", url.AbsoluteUri, "replace", "-1", options).ContinueWith(_ => { });
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            Failed?.Invoke(error.Message);
        }
    }

    public bool Paused
    {
        set { if (mpv is not null) Try(() => mpv.SetProperty("pause", value)); }
    }

    /// <summary>The playback speed; voices keep their pitch (mpv's scaletempo).</summary>
    public double Speed
    {
        set => Try(() => Mpv.SetProperty("speed", value));
    }

    public void Seek(double seconds)
    {
        if (mpv is null) return;
        _ = mpv.CommandAsync("seek", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture), "absolute+exact").ContinueWith(_ => { });
    }

    /// <summary>Seconds into the file, or null with nothing playing.</summary>
    public double? Position => mpv?.GetDouble("time-pos");

    /// <summary>The file's length once known.</summary>
    public double? Duration => mpv?.GetDouble("duration");

    public void Stop()
    {
        if (mpv is not null) Try(() => mpv.Command("stop"));
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (MpvException) { }
    }

    public void Dispose()
    {
        mpv?.Dispose();
        mpv = null;
    }
}
