using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Audio;

namespace Aster.Desktop.Audio;

/// <summary>
/// The confirmed Sonos's own volume (the one turned in passthrough, or
/// chosen then: settings' SonosSpeaker), for sound outside the video
/// player (music, audiobooks, a trailer), as a TV's remote turns it. Only
/// while it's on its TV input, checked again each minute: without one, the
/// caller turns its own volume. Never searches for another or asks which.
/// </summary>
public static class SoundbarVolume
{
    private static readonly SemaphoreSlim Turn = new(1, 1);
    private static SonosSpeaker? speaker;
    private static DateTime checkedAt;

    /// <summary>The confirmed Sonos turned by <paramref name="by"/> (of 100): its room and level, or null when there's none to turn.</summary>
    public static async Task<(string Room, int Level)?> ChangeAsync(int by)
    {
        if (AppSettings.Current.SonosSpeaker is not { } confirmed) return null;
        await Turn.WaitAsync();
        try
        {
            if (speaker?.Id != confirmed || DateTime.UtcNow - checkedAt > TimeSpan.FromMinutes(1))
            {
                speaker = (await Sonos.OnTvInputAsync()).FirstOrDefault(s => s.Id == confirmed);
                checkedAt = DateTime.UtcNow;
            }
            if (speaker is null) return null;
            if (await Sonos.ChangeVolumeAsync(speaker, by) is { } level) return (speaker.Room, level);
            speaker = null;
            return null;
        }
        finally { Turn.Release(); }
    }
}
