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
public sealed class AudioEngine : IAudioOutput
{
    private MpvPlayer? mpv;

    /// <summary>A file opened and is ready.</summary>
    public event Action? Loaded;

    /// <summary>The file played to its end.</summary>
    public event Action? Ended;

    /// <summary>The file played to its end, and the one queued after it (<see cref="QueueNext"/>) runs on from it, with no gap.</summary>
    public event Action? Advanced;

    /// <summary>What's queued to run on after the file playing, if anything.</summary>
    private Uri? queued;

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
                // A song queued after the one playing runs on from it without a pause: an album's
                // tracks as one. Weak: where the two differ (a FLAC after an MP3) the sound reopens.
                ["gapless-audio"] = "weak",
                ["prefetch-playlist"] = "yes",
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
                if (end.Reason == MpvEndFileReason.Eof) Dispatcher.UIThread.Post(() =>
                {
                    // The queued one is playing now, mpv having moved on to it by itself.
                    if (queued is not null)
                    {
                        queued = null;
                        Advanced?.Invoke();
                    }
                    else Ended?.Invoke();
                });
                else if (end.Reason == MpvEndFileReason.Error) Dispatcher.UIThread.Post(() => Failed?.Invoke(end.ErrorMessage ?? "This can't be played."));
            };
            return mpv;
        }
    }

    public bool IsRemote => false;

    public bool CanChangeSpeed => true;

    /// <summary>Opens <paramref name="url"/> at <paramref name="start"/> seconds, playing or paused there.</summary>
    public void Load(Uri url, double start, bool playing, Aster.Core.Audio.SonosTrack? track = null)
    {
        try
        {
            Mpv.SetProperty("pause", !playing);
            // Replacing it clears mpv's playlist, the queued one with it.
            queued = null;
            var options = start > 0 ? "start=" + start.ToString("0.###", CultureInfo.InvariantCulture) : "";
            _ = Mpv.CommandAsync("loadfile", url.AbsoluteUri, "replace", "-1", options).ContinueWith(_ => { });
        }
        catch (Exception error) when (error is MpvException or DllNotFoundException)
        {
            Failed?.Invoke(error.Message);
        }
    }

    /// <summary>
    /// <paramref name="url"/> to play on from the file playing as it ends, with
    /// no gap (mpv opens it ahead); null for nothing, as it ends.
    /// </summary>
    public void QueueNext(Uri? url)
    {
        if (mpv is null || url == queued) return;
        try
        {
            // Everything but the file playing goes.
            mpv.Command("playlist-clear");
            if (url is not null) _ = mpv.CommandAsync("loadfile", url.AbsoluteUri, "append").ContinueWith(_ => { });
            queued = url;
        }
        catch (MpvException) { queued = null; }
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

    /// <summary>Its own volume turned by <paramref name="by"/> (of 100): the level after, or null with nothing loaded.</summary>
    public double? ChangeVolume(double by)
    {
        if (mpv?.GetDouble("volume") is not { } volume) return null;
        var level = Math.Clamp(volume + by, 0, 100);
        mpv.SetProperty("volume", level);
        return level;
    }

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
