namespace Aster.Core.Playback;

public static class StreamQuality
{
    /// <summary>The caps offered, in bits per second, highest first.</summary>
    public static readonly IReadOnlyList<int> Caps = [20_000_000, 10_000_000, 4_000_000];

    /// <summary>"20 Mbps".</summary>
    public static string Title(int cap) => $"{cap / 1_000_000} Mbps";
}

/// <summary>
/// Decides when stalling has gone on long enough to offer a lower quality:
/// one stall that lasts, or stalls that keep coming back. Turning the offer
/// down leaves it alone for a while.
/// </summary>
public sealed class StallWatch
{
    /// <summary>A single stall this long brings the offer up.</summary>
    public TimeSpan LongStall { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>So does this many stalls inside <see cref="Window"/>.</summary>
    public int Repeats { get; init; } = 3;

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long "Keep trying" holds the offer off.</summary>
    public TimeSpan QuietAfterDecline { get; init; } = TimeSpan.FromSeconds(120);

    private DateTimeOffset? stallStart;
    private readonly List<DateTimeOffset> stalls = [];
    private DateTimeOffset? quietUntil;

    /// <summary>Takes whether playback is held up right now; true when it's time to offer a lower quality.</summary>
    public bool Note(bool stalling, DateTimeOffset now)
    {
        if (!stalling)
        {
            stallStart = null;
            return false;
        }
        if (stallStart is null)
        {
            stallStart = now;
            stalls.Add(now);
        }
        stalls.RemoveAll(s => now - s > Window);
        if (quietUntil is { } quiet && now < quiet) return false;
        var lasting = now - stallStart >= LongStall;
        return lasting || stalls.Count >= Repeats;
    }

    /// <summary>The offer was turned down: hold it off for a while, then count afresh.</summary>
    public void Declined(DateTimeOffset now)
    {
        quietUntil = now + QuietAfterDecline;
        stalls.Clear();
        if (stallStart is not null) stallStart = now;
    }
}
