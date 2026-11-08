using System.Diagnostics;
using System.Globalization;

namespace Aster.Core.Diagnostics;

/// <summary>
/// How long a video took to start, step by step: the item, the server's
/// playback answer, the stream opened, and the first picture. Told in a
/// debug report's "This run". The Android app's StartTiming.
/// </summary>
public sealed class StartTiming
{
    private readonly Func<long> now;
    private readonly List<(string Step, long At)> marks = [];
    private readonly long began;

    /// <summary>The clock starts now; <paramref name="now"/> reads it in milliseconds (a test's own).</summary>
    public StartTiming(Func<long>? now = null)
    {
        this.now = now ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        began = this.now();
    }

    /// <summary>"direct play" or "transcode", once known.</summary>
    public string? Method { get; set; }

    /// <summary>True once a summary's been told, so it's told once.</summary>
    public bool Told { get; set; }

    public void Mark(string step, bool once = false)
    {
        if (once && marks.Any(m => m.Step == step)) return;
        marks.Add((step, now()));
    }

    /// <summary>"Start (transcode) took 6.42 s: item 0.12 s, server's playback answer +0.30 s, …".</summary>
    public string Summary(bool leftEarly = false)
    {
        var start = began;
        var steps = marks.Select((m, i) =>
        {
            var since = m.At - (i == 0 ? start : marks[i - 1].At);
            return $"{m.Step} {(i == 0 ? "" : "+")}{Seconds(since)}";
        }).ToList();
        var how = Method is { } method ? $" ({method})" : "";
        if (leftEarly)
            return $"Start{how} left after {Seconds(now() - start)} before the first picture" + (steps.Count == 0 ? "" : $"; it had reached: {string.Join(", ", steps)}");
        var total = (marks.Count > 0 ? marks[^1].At : start) - start;
        return $"Start{how} took {Seconds(total)}: {string.Join(", ", steps)}";
    }

    private static string Seconds(long ms) => (ms / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " s";
}
