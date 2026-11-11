using System;

namespace Aster.Desktop.Audio;

/// <summary>A video playing, as the system's media controls show and drive it.</summary>
public interface IVideoMediaSource
{
    /// <summary>What it shows; null while there's nothing to show yet.</summary>
    NowPlayingInfo? NowPlaying { get; }

    void Run(MediaCommand command);

    void SeekTo(double seconds);
}

/// <summary>
/// The video playing, if one is, for the system's media controls: shown in
/// place of a song or book (a video pauses those), as the Apple app shows
/// it in the Mac's Now Playing. The player sets it, and says when it changes.
/// </summary>
public static class VideoMedia
{
    public static IVideoMediaSource? Current { get; private set; }

    /// <summary>The video, its title, state or place changed: the controls are told.</summary>
    public static event Action? Changed;

    public static void Update(IVideoMediaSource source)
    {
        Current = source;
        Changed?.Invoke();
    }

    /// <summary>That video's closed; a later one may already have taken its place.</summary>
    public static void Clear(IVideoMediaSource source)
    {
        if (Current != source) return;
        Current = null;
        Changed?.Invoke();
    }
}
