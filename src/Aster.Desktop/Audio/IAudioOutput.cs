using System;
using Aster.Core.Audio;

namespace Aster.Desktop.Audio;

/// <summary>
/// Where songs and books play: this computer (AudioEngine, mpv) or a group
/// of Sonos speakers (SonosOutput), which fetch the stream themselves. The
/// players drive either the same way. Events come back on the UI thread.
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>A file opened and is ready.</summary>
    event Action? Loaded;

    /// <summary>The file played to its end.</summary>
    event Action? Ended;

    /// <summary>The file couldn't be played; why.</summary>
    event Action<string>? Failed;

    /// <summary>Speakers on the network rather than this computer (they're given a stream they can fetch).</summary>
    bool IsRemote { get; }

    /// <summary>Whether it can play faster or slower (Sonos can't).</summary>
    bool CanChangeSpeed { get; }

    /// <summary>Opens <paramref name="url"/> at <paramref name="start"/> seconds, playing or paused; <paramref name="track"/> is what it is, for speakers that show it.</summary>
    void Load(Uri url, double start, bool playing, SonosTrack? track = null);

    bool Paused { set; }

    double Speed { set; }

    void Seek(double seconds);

    /// <summary>Seconds into the file, or null with nothing playing.</summary>
    double? Position { get; }

    /// <summary>The file's length once known.</summary>
    double? Duration { get; }

    /// <summary>Its own volume turned by <paramref name="by"/> (of 100): the level after, or null when it can't say at once.</summary>
    double? ChangeVolume(double by);

    void Stop();
}
