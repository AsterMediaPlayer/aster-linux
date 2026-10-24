// The button sounds' presets (CLIENT_GUIDE §5.5): click, chime and beep,
// synthesised exactly as the reference renderer's WebAudio does. A port of
// the Apple app's DiscToneSynth (in DiscMenuAudio.swift).

namespace Aster.Core.DiscMenu;

public static class DiscTones
{
    public const int SampleRate = 44_100;

    private sealed record Tone(double Frequency, bool Square, double Duration, double Gain, double Start = 0);

    private static Tone[] Tones(string preset, DiscSound kind) => (preset, kind) switch
    {
        ("click", DiscSound.Move) => [new(1500, true, 0.025, 0.5)],
        ("click", DiscSound.Select) => [new(1100, true, 0.04, 0.6), new(700, true, 0.05, 0.5, 0.04)],
        ("click", DiscSound.Back) => [new(600, true, 0.05, 0.6)],
        ("chime", DiscSound.Move) => [new(1320, false, 0.12, 0.4)],
        ("chime", DiscSound.Select) => [new(880, false, 0.2, 0.6), new(1320, false, 0.3, 0.5, 0.08)],
        ("chime", DiscSound.Back) => [new(660, false, 0.2, 0.5), new(440, false, 0.25, 0.5, 0.08)],
        ("beep", DiscSound.Move) => [new(880, false, 0.04, 0.5)],
        ("beep", DiscSound.Select) => [new(1040, false, 0.09, 0.6)],
        ("beep", DiscSound.Back) => [new(520, false, 0.09, 0.6)],
        _ => [],
    };

    /// <summary>
    /// The preset's sound for <paramref name="kind"/> as mono samples at
    /// <see cref="SampleRate"/>, or null for none (an unknown preset, or
    /// "none"). Each tone ramps exponentially from 0.0001 to
    /// max(0.0002, volume × gain × 0.3) over its first 5 ms, then back down
    /// to 0.0001 by its end.
    /// </summary>
    public static float[]? Render(string? preset, DiscSound kind, double volume)
    {
        var tones = Tones(preset ?? "none", kind);
        if (tones.Length == 0) return null;
        var total = tones.Max(t => t.Start + t.Duration);
        var frames = Math.Max(1, (int)(total * SampleRate) + 64);
        var samples = new float[frames];
        foreach (var tone in tones)
        {
            var peak = Math.Max(0.0002, volume * tone.Gain * 0.3);
            var startFrame = (int)(tone.Start * SampleRate);
            var count = (int)(tone.Duration * SampleRate);
            var attack = Math.Max(1, (int)(0.005 * SampleRate));
            for (var i = 0; i < count; i++)
            {
                var t = (double)i / SampleRate;
                var envelope = i < attack
                    ? 0.0001 * Math.Pow(peak / 0.0001, (double)i / attack)
                    : peak * Math.Pow(0.0001 / peak, (double)(i - attack) / Math.Max(1, count - attack));
                var phase = Math.Sin(2 * Math.PI * tone.Frequency * t);
                var sample = tone.Square ? (phase >= 0 ? 1.0 : -1.0) : phase;
                var index = startFrame + i;
                if (index < frames) samples[index] += (float)(sample * envelope);
            }
        }
        return samples;
    }
}
