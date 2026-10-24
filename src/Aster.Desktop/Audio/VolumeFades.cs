using System;
using System.Collections.Generic;
using Aster.Mpv;
using Avalonia.Threading;

namespace Aster.Desktop.Audio;

/// <summary>
/// Volume fades of looping background players (theme music, a disc menu's
/// music), stepped 30 times a second on the UI thread.
/// </summary>
public sealed class VolumeFades
{
    /// <summary>The fades under way, by player.</summary>
    private readonly Dictionary<MpvPlayer, DispatcherTimer> fades = [];

    /// <summary>mpv's volume is cubic: a linear level from 0 to 1 (the Apple app's) as mpv's 0 to 100.</summary>
    public static double MpvVolume(double level) => Math.Cbrt(Math.Clamp(level, 0, 1)) * 100;

    /// <summary>
    /// Moves <paramref name="player"/>'s volume to <paramref name="to"/>, then
    /// calls <paramref name="then"/>. A new fade of the same player takes over
    /// from the last, whose <paramref name="then"/> never comes.
    /// </summary>
    public void Fade(MpvPlayer player, double to, double seconds, Action? then = null)
    {
        if (fades.Remove(player, out var previous)) previous.Stop();
        double from = 0;
        Try(() => from = player.GetDouble("volume") ?? 0);
        var steps = Math.Max(1, (int)(seconds * 30));
        var step = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds / steps) };
        timer.Tick += (_, _) =>
        {
            step++;
            Try(() => player.SetProperty("volume", from + (to - from) * step / steps));
            if (step < steps) return;
            timer.Stop();
            fades.Remove(player);
            then?.Invoke();
        };
        fades[player] = timer;
        timer.Start();
    }

    /// <summary>Ends every fade at once; players still fading out are let go.</summary>
    public void StopAll()
    {
        foreach (var (player, timer) in fades)
        {
            timer.Stop();
            player.Dispose();
        }
        fades.Clear();
    }

    private static void Try(Action action)
    {
        try { action(); }
        catch (Exception error) when (error is MpvException or ObjectDisposedException) { }
    }
}
