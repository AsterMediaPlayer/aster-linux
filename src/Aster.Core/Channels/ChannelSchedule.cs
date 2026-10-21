using Aster.Core.Jellyfin;

namespace Aster.Core.Channels;

// What's on a Channel, worked out from the clock: a port of the Apple app's
// ChannelSchedule.swift, kept to it exactly, so a Mac, an Apple TV and this
// PC all show the same thing at the same moment with nothing stored and no
// server involved.
//
// A channel is a playlist of movies and TV episodes, grouped into shows (a
// series, or a single movie). Each day the shows take turns in a shuffled
// order. The timeline runs back to back from a fixed start (local midnight,
// 1 January 2026). Each day picks up where the day before ended: whatever is
// playing at midnight finishes first, then the new day's order begins, going
// round it again until the next midnight. A channel can keep its order for a
// week or a month instead. A show plays its episodes in order and carries on
// from where it got to, like reruns.
//
// A channel with break clips has a break after its programmes: a bumper out,
// commercials, promos, trailers, interstitials, a bumper back in, dealt from
// the channel's seed and the break's start, so it's the same everywhere too.
// With `align`, each break runs on to the next mark (the half hour, say).
//
// Times are kept as Swift keeps a Date: a double of seconds since 1 January
// 2001 (UTC), added to in the same order, so they round the same way.

/// How a channel's shows take turns.
public enum ChannelMode
{
    /// Shows take turns, one episode each.
    Rotation,
    /// Shows take turns, two to four episodes in a row each.
    Blocks,
    /// Every movie and episode on its own, in a new order each day.
    Shuffle,
    /// The playlist's own order, straight through and round again.
    Sequential,
    /// Everything in the order it first aired, across every show.
    Airdate,
}

/// How long a channel keeps one shuffled order before dealing a new one.
public enum ChannelReshuffle
{
    Day,
    /// From Monday to Monday.
    Week,
    /// From the 1st to the 1st.
    Month,
}

/// What a break clip is, as its own tag says (<c>aster:bumper</c>, <c>aster:trailer</c>).
public enum BreakKind
{
    Bumpers,
    Promos,
    Commercials,
    Trailers,
    Interstitials,
}

/// Where a channel's bumpers go in its breaks.
public enum BumperPlacement
{
    /// One leads into the break and one comes back out of it.
    Both,
    /// Only into the break ("We'll be right back").
    Out,
    /// Only out of it ("Now back to…").
    In,
}

/// What a scheduled item is to its channel: a programme, or part of a break.
public enum ProgramRole
{
    Programme,
    Bumper,
    Promo,
    Commercial,
    Trailer,
    Interstitial,
}

public static class ChannelNames
{
    extension(ChannelMode mode)
    {
        /// Its name in tags.
        public string Tag => mode.ToString().ToLowerInvariant();

        public string Title => mode switch
        {
            ChannelMode.Blocks => "Blocks",
            ChannelMode.Shuffle => "Shuffle",
            ChannelMode.Sequential => "In Order",
            ChannelMode.Airdate => "Air Date",
            _ => "Rotation",
        };

        /// Whether the order is dealt again each period; the two fixed orders aren't.
        public bool Reshuffles => mode is not (ChannelMode.Sequential or ChannelMode.Airdate);
    }

    extension(ChannelReshuffle reshuffle)
    {
        public string Tag => reshuffle.ToString().ToLowerInvariant();

        public string Title => reshuffle switch
        {
            ChannelReshuffle.Week => "Weekly",
            ChannelReshuffle.Month => "Monthly",
            _ => "Daily",
        };
    }

    extension(BreakKind kind)
    {
        public string Tag => kind.ToString().ToLowerInvariant();
        public string Title => kind.ToString();
    }

    extension(BumperPlacement placement)
    {
        public string Tag => placement.ToString().ToLowerInvariant();

        public string Title => placement switch
        {
            BumperPlacement.Out => "Before the Break",
            BumperPlacement.In => "After the Break",
            _ => "Before & After",
        };
    }

    extension(ProgramRole role)
    {
        public bool IsBreak => role != ProgramRole.Programme;
    }
}

/// How a channel's breaks between programmes are made up, from its tags.
public sealed record ChannelBreaks
{
    public const int DefaultMinutes = 2;

    public BumperPlacement BumpersAt { get; init; } = BumperPlacement.Both;
    public int PromoCount { get; init; } = 1;

    /// Trailers in each break.
    public int TrailerCount { get; init; } = 1;

    /// Interstitials in each break, without <see cref="Align"/> (which fills with them).
    public int InterstitialCount { get; init; } = 1;

    /// Minutes of commercials in each break.
    public int Minutes { get; init; } = DefaultMinutes;

    /// A break after every this many programmes, without <see cref="Align"/>.
    public int Every { get; init; } = 1;

    /// Programmes start on a multiple of this many minutes past the hour, each break running on to the mark; 0 for off.
    public int Align { get; init; }

    /// Lined up, the last filler clip is cut short so programmes start exactly on the mark;
    /// otherwise (best effort) clips play whole and a programme can start a few seconds before it.
    public bool ExactAlign { get; init; }
}

/// The clips a channel's breaks play, sorted by kind from its filler playlists.
public sealed record ChannelBreakClips
{
    public IReadOnlyList<BaseItem> Bumpers { get; init; } = [];
    public IReadOnlyList<BaseItem> Promos { get; init; } = [];
    public IReadOnlyList<BaseItem> Commercials { get; init; } = [];
    public IReadOnlyList<BaseItem> Trailers { get; init; } = [];
    public IReadOnlyList<BaseItem> Interstitials { get; init; } = [];

    public static readonly ChannelBreakClips None = new();

    public bool IsEmpty => Bumpers.Count == 0 && Promos.Count == 0 && Commercials.Count == 0 && Trailers.Count == 0 && Interstitials.Count == 0;
}

/// How a channel's lineup is dealt, from its tags.
public sealed record ChannelLineup
{
    public static readonly (int Low, int High) DefaultBlocks = (2, 4);

    public ChannelMode Mode { get; init; } = ChannelMode.Rotation;
    public ChannelReshuffle Reshuffle { get; init; } = ChannelReshuffle.Day;

    /// Mixed into the shuffle, so another number deals a fresh lineup; 0 is the channel's original one.
    public int Seed { get; init; }

    /// How many episodes a show plays in a row in Blocks mode.
    public (int Low, int High) Blocks { get; init; } = DefaultBlocks;

    /// Shows, by lower-cased name, that take this many turns each round.
    public IReadOnlyDictionary<string, int> Weights { get; init; } = new Dictionary<string, int>();

    /// Titles shorter or longer than these, in seconds, are left out.
    public double? MinLength { get; init; }
    public double? MaxLength { get; init; }

    /// How its breaks are made up, when it has clips for them.
    public ChannelBreaks Breaks { get; init; } = new();

    /// Whether <paramref name="length"/> seconds is within the channel's limits.
    public bool Allows(double length) => (MinLength is not { } min || length >= min) && (MaxLength is not { } max || length <= max);
}

/// A movie, episode or break clip on a channel, from <see cref="StartSeconds"/> to <see cref="EndSeconds"/>.
public sealed record ScheduledProgram(string ChannelId, BaseItem Item, double StartSeconds, double EndSeconds, ProgramRole Role = ProgramRole.Programme)
{
    /// Swift's reference date, which <see cref="StartSeconds"/> and <see cref="EndSeconds"/> count from.
    public static readonly DateTimeOffset ReferenceDate = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// Seconds between 1970 and the reference date, as Foundation adds them.
    public const double Since1970 = 978307200.0;

    /// Unique even when an item comes round again.
    public string Id => $"{ChannelId}|{(long)(StartSeconds + Since1970)}";

    public DateTimeOffset Start => ToDate(StartSeconds);
    public DateTimeOffset End => ToDate(EndSeconds);
    public double Duration => EndSeconds - StartSeconds;

    /// How far into the item <paramref name="date"/> falls, in seconds.
    public double Offset(DateTimeOffset date) => Math.Min(Math.Max(ToSeconds(date) - StartSeconds, 0), Duration);

    public bool Contains(DateTimeOffset date)
    {
        var seconds = ToSeconds(date);
        return StartSeconds <= seconds && seconds < EndSeconds;
    }

    public static DateTimeOffset ToDate(double seconds) => ReferenceDate.AddTicks((long)Math.Round(seconds * 10_000_000));
    public static double ToSeconds(DateTimeOffset date) => (date - ReferenceDate).Ticks / 10_000_000.0;
}

public static class ChannelSchedule
{
    /// <summary>An item shorter than this (or with no runtime) isn't scheduled.</summary>
    public const double MinimumLength = 60;

    /// <summary>A break clip shorter than this isn't played.</summary>
    public const double ShortestClip = 1;

    /// <summary>As far as a break can run past its programme: an hour's align, or the longest break and its bumpers and promos.</summary>
    public const double LongestBreak = 2 * 3600;

    /// <summary>Where every channel's timeline begins: local midnight, 1 January 2026.</summary>
    public static double Epoch(TimeZoneInfo zone) => FromLocal(new DateTime(2026, 1, 1), zone);

    /// <summary>The channel's programmes overlapping <paramref name="from"/> to <paramref name="to"/>, in order.</summary>
    public static IReadOnlyList<ScheduledProgram> Programs(string channelId, IReadOnlyList<BaseItem> items, ChannelLineup lineup, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null) =>
        Segments(channelId, items, lineup, ChannelBreakClips.None, from, to, zone);

    /// <summary>
    /// The channel's programmes overlapping <paramref name="from"/> to
    /// <paramref name="to"/>, each taking in the break after it, as a guide
    /// shows them: back to back, with no row for each clip.
    /// </summary>
    public static IReadOnlyList<ScheduledProgram> Programs(string channelId, IReadOnlyList<BaseItem> items, ChannelLineup lineup, ChannelBreakClips clips, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null)
    {
        if (clips.IsEmpty) return Segments(channelId, items, lineup, ChannelBreakClips.None, from, to, zone);
        // From far enough back to catch the programme whose break is on at
        // `from`, and on far enough to find where the last one's break ends.
        var raw = Segments(channelId, items, lineup, clips, from.AddSeconds(-LongestBreak), to.AddSeconds(LongestBreak), zone);
        var folded = new List<ScheduledProgram>();
        for (var index = 0; index < raw.Count; index++)
        {
            var segment = raw[index];
            if (segment.Role != ProgramRole.Programme) continue;
            var next = raw.Skip(index + 1).FirstOrDefault(s => s.Role == ProgramRole.Programme);
            var end = next?.StartSeconds ?? (raw.Count > 0 ? raw[^1].EndSeconds : segment.EndSeconds);
            folded.Add(new ScheduledProgram(segment.ChannelId, segment.Item, segment.StartSeconds, end));
        }
        var fromSeconds = ScheduledProgram.ToSeconds(from);
        var toSeconds = ScheduledProgram.ToSeconds(to);
        return folded.Where(p => p.EndSeconds > fromSeconds && p.StartSeconds < toSeconds).ToList();
    }

    /// <summary>Everything the channel plays overlapping the window, programmes and break clips alike, in order: what a player goes through.</summary>
    public static IReadOnlyList<ScheduledProgram> Segments(string channelId, IReadOnlyList<BaseItem> items, ChannelLineup lineup, ChannelBreakClips clips, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? zone = null)
    {
        var fromSeconds = ScheduledProgram.ToSeconds(from);
        var toSeconds = ScheduledProgram.ToSeconds(to);
        var found = new List<ScheduledProgram>();
        Walk(channelId, items, lineup, clips, fromSeconds, toSeconds, zone ?? TimeZoneInfo.Local, program =>
        {
            if (program.EndSeconds > fromSeconds && program.StartSeconds < toSeconds) found.Add(program);
        });
        return found;
    }

    /// <summary>The programme on the channel at <paramref name="date"/>.</summary>
    public static ScheduledProgram? Airing(string channelId, IReadOnlyList<BaseItem> items, ChannelLineup lineup, DateTimeOffset date, TimeZoneInfo? zone = null) =>
        Programs(channelId, items, lineup, date, date.AddSeconds(1), zone).FirstOrDefault();

    // ---- Shows ----

    /// <summary>An item with the seconds it runs for.</summary>
    private sealed record Entry(BaseItem Item, double Length);

    /// <summary>A series' episodes in order, or a single movie.</summary>
    private sealed record Show(string Id, IReadOnlyList<Entry> Episodes)
    {
        /// <summary>The series' or movie's name, for weight=.</summary>
        public string Name => Episodes.FirstOrDefault()?.Item is not { } item ? ""
            : item.Kind == ItemKind.Episode ? item.SeriesName ?? item.DisplayTitle : item.DisplayTitle;
    }

    private static double Seconds(BaseItem item) => (item.RunTimeTicks ?? 0) / 10_000_000.0;

    /// <summary>
    /// The items grouped into shows, sorted by id so devices that list the
    /// playlist in another order still shuffle the same list. Specials
    /// (season 0), missing episodes and items without a runtime are left out.
    /// </summary>
    private static List<Show> Shows(IReadOnlyList<BaseItem> items, ChannelMode mode)
    {
        switch (mode)
        {
            case ChannelMode.Sequential:
            {
                // One "show": the whole playlist in its own order, specials and all.
                var seen = new HashSet<string>();
                var entries = items.Where(i => Seconds(i) >= MinimumLength && i.LocationType != "Virtual" && seen.Add(i.Id))
                    .Select(i => new Entry(i, Seconds(i))).ToList();
                return entries.Count == 0 ? [] : [new Show("playlist", entries)];
            }
            case ChannelMode.Shuffle:
                // Every item its own "show", so episodes mix across series.
                return Shows(items, ChannelMode.Rotation)
                    .SelectMany(show => show.Episodes.Select(e => new Show(e.Item.Id, [e])))
                    .OrderBy(s => s.Id, StringComparer.Ordinal)
                    .ToList();
            case ChannelMode.Airdate:
            {
                // One "show": everything by when it first aired; undated titles last, in a fixed order.
                var entries = Shows(items, ChannelMode.Rotation).SelectMany(s => s.Episodes).ToList();
                entries.Sort((a, b) =>
                {
                    var byDate = string.CompareOrdinal(a.Item.PremiereDate ?? "~", b.Item.PremiereDate ?? "~");
                    if (byDate != 0) return byDate;
                    var bySeries = string.CompareOrdinal(a.Item.SeriesName ?? "", b.Item.SeriesName ?? "");
                    if (bySeries != 0) return bySeries;
                    var bySeason = (a.Item.ParentIndexNumber ?? 0).CompareTo(b.Item.ParentIndexNumber ?? 0);
                    if (bySeason != 0) return bySeason;
                    var byNumber = (a.Item.IndexNumber ?? 0).CompareTo(b.Item.IndexNumber ?? 0);
                    return byNumber != 0 ? byNumber : string.CompareOrdinal(a.Item.Id, b.Item.Id);
                });
                return entries.Count == 0 ? [] : [new Show("airdate", entries)];
            }
        }
        var seenIds = new HashSet<string>();
        var series = new Dictionary<string, List<Entry>>();
        var shows = new List<Show>();
        foreach (var item in items)
        {
            var length = Seconds(item);
            if (!(length >= MinimumLength && item.LocationType != "Virtual" && seenIds.Add(item.Id))) continue;
            var entry = new Entry(item, length);
            if (item.Kind == ItemKind.Episode && item.SeriesId is { } seriesId)
            {
                if ((item.ParentIndexNumber ?? 1) == 0) continue;
                if (!series.TryGetValue(seriesId, out var list)) series[seriesId] = list = [];
                list.Add(entry);
            }
            else
            {
                shows.Add(new Show(item.Id, [entry]));
            }
        }
        foreach (var (id, episodes) in series)
        {
            var ordered = episodes.ToList();
            ordered.Sort((a, b) =>
            {
                var bySeason = (a.Item.ParentIndexNumber ?? 1).CompareTo(b.Item.ParentIndexNumber ?? 1);
                if (bySeason != 0) return bySeason;
                var byNumber = (a.Item.IndexNumber ?? int.MaxValue).CompareTo(b.Item.IndexNumber ?? int.MaxValue);
                return byNumber != 0 ? byNumber : string.CompareOrdinal(a.Item.Id, b.Item.Id);
            });
            shows.Add(new Show(id, ordered));
        }
        return shows.OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
    }

    // ---- Building the timeline ----

    /// <summary>
    /// Where the walk has got to at the start of a period (a day, week or
    /// month, as the channel reshuffles): enough to carry on from there
    /// without going back to the epoch.
    /// </summary>
    private sealed class WalkState
    {
        public double PeriodStart;
        /// <summary>When the period's first program starts (after the one crossing into it).</summary>
        public double Time;
        /// <summary>Periods since the epoch.</summary>
        public ulong Period;
        /// <summary>The show that played last, by its place in the list.</summary>
        public int? LastShow;
        /// <summary>The next episode of each show, counted from its first.</summary>
        public int[] Pointers = [];
        /// <summary>Programmes since the last break, for break-every.</summary>
        public int SinceBreak;

        public WalkState Copy() => new()
        {
            PeriodStart = PeriodStart, Time = Time, Period = Period, LastShow = LastShow, Pointers = (int[])Pointers.Clone(), SinceBreak = SinceBreak,
        };
    }

    /// <summary>The latest period-start state reached for each channel and list, so asking again later resumes there.</summary>
    private static readonly Dictionary<string, WalkState> Checkpoints = [];
    private static readonly Lock CheckpointGate = new();

    /// <summary>Lays the timeline out up to <paramref name="end"/>, handing each program to <paramref name="visit"/> as it goes. Programs before <paramref name="from"/> may be skipped.</summary>
    private static void Walk(string channelId, IReadOnlyList<BaseItem> items, ChannelLineup lineup, ChannelBreakClips clips, double from, double end, TimeZoneInfo zone, Action<ScheduledProgram> visit)
    {
        var limited = lineup.MinLength is null && lineup.MaxLength is null ? items : items.Where(i => lineup.Allows(Seconds(i))).ToList();
        var shows = Shows(limited, lineup.Mode);
        if (shows.Count == 0) return;
        var pool = new BreakPool(clips);
        // How many turns each show takes a round: 1 unless weight= says.
        int[] turns = lineup.Weights.Count == 0 ? [] : shows.Select(s => Math.Max(1, lineup.Weights.GetValueOrDefault(s.Name.ToLowerInvariant(), 1))).ToArray();

        var channelSeed = Seed(channelId);
        var @base = lineup.Seed == 0 ? channelSeed : Mix(channelSeed ^ unchecked((ulong)(long)lineup.Seed));
        var blocks = lineup.Blocks;
        var blockCount = (ulong)(blocks.High - blocks.Low + 1);
        var key = string.Join("|",
            channelId, lineup.Mode.Tag, lineup.Reshuffle.Tag, lineup.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"{blocks.Low}-{blocks.High}", zone.Id,
            string.Join(",", lineup.Weights.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => $"{w.Key}={w.Value}")),
            $"{lineup.MinLength ?? 0}-{lineup.MaxLength ?? 0}",
            Seed(string.Join(",", shows.SelectMany(s => s.Episodes.Select(e => e.Item.Id)))).ToString(System.Globalization.CultureInfo.InvariantCulture))
            + (pool.IsEmpty ? "" : "|" + pool.Key(lineup.Breaks));
        var start = Epoch(zone);
        WalkState? saved;
        lock (CheckpointGate) saved = Checkpoints.GetValueOrDefault(key)?.Copy();
        var state = saved is { } resume && resume.Time <= from ? resume
            : new WalkState { PeriodStart = start, Time = start, Period = 0, LastShow = null, Pointers = new int[shows.Count] };
        WalkState? latest = null;

        while (state.Time < end)
        {
            var periodEnd = EndOfPeriod(state.PeriodStart, lineup.Reshuffle, zone);
            if (state.Time <= from) latest = state.Copy();
            var periodSeed = @base ^ unchecked(state.Period * 0x9E37_79B9_7F4A_7C15UL);
            var order = Shuffled(Enumerable.Range(0, shows.Count).ToArray(), periodSeed);
            if (turns.Any(t => t > 1)) order = Weighted(order, turns, periodSeed);
            // A new period never opens with the show that just played.
            if (order.Length > 1 && order[0] == state.LastShow) (order[0], order[^1]) = (order[^1], order[0]);
            // The shows go round this order until the period ends, so in a
            // week or a month the turns carry on across midnight.
            var turn = 0;
            while (state.Time < periodEnd && state.Time < end)
            {
                var index = order[turn % order.Length];
                var show = shows[index];
                var size = lineup.Mode == ChannelMode.Blocks
                    ? Math.Min(show.Episodes.Count, blocks.Low + (int)(Mix(periodSeed ^ unchecked((ulong)(turn + 1) * 0xD1B5_4A32_D192_ED03UL)) % blockCount))
                    : 1;
                var played = 0;
                while (played < size && state.Time < periodEnd && state.Time < end)
                {
                    var entry = show.Episodes[state.Pointers[index] % show.Episodes.Count];
                    var finish = state.Time + entry.Length;
                    visit(new ScheduledProgram(channelId, entry.Item, state.Time, finish));
                    state.Time = finish;
                    state.Pointers[index] += 1;
                    played += 1;
                    if (!pool.IsEmpty)
                    {
                        state.SinceBreak += 1;
                        // Lined up, every programme needs its break to reach the mark.
                        if (lineup.Breaks.Align > 0 || state.SinceBreak >= lineup.Breaks.Every)
                        {
                            state.SinceBreak = 0;
                            foreach (var clip in pool.Deal(state.Time, lineup.Breaks, @base, zone))
                            {
                                visit(new ScheduledProgram(channelId, clip.Item, clip.Start, clip.End, clip.Role));
                                state.Time = clip.End;
                            }
                        }
                    }
                }
                state.LastShow = index;
                turn += 1;
            }
            state.PeriodStart = periodEnd;
            state.Period += 1;
            // The period's last program can run past the next one's end only
            // when it's longer than a period; skip the periods it covers.
            while (EndOfPeriod(state.PeriodStart, lineup.Reshuffle, zone) is var next && next <= state.Time)
            {
                state.PeriodStart = next;
                state.Period += 1;
            }
        }

        if (latest is not null && latest.Time > (saved?.Time ?? double.NegativeInfinity))
        {
            lock (CheckpointGate) Checkpoints[key] = latest;
        }
    }

    // ---- Breaks ----

    /// <summary>A channel's break clips that can play (a second or longer, and on the server), sorted by id so every device deals from the same list.</summary>
    private sealed class BreakPool
    {
        private readonly List<Entry> bumpers, openers, closers, promos, commercials, trailers, interstitials;

        public BreakPool(ChannelBreakClips clips)
        {
            static List<Entry> Playable(IReadOnlyList<BaseItem> items)
            {
                var seen = new HashSet<string>();
                return items.Where(i => Seconds(i) >= ShortestClip && i.LocationType != "Virtual" && seen.Add(i.Id))
                    .Select(i => new Entry(i, Seconds(i)))
                    .OrderBy(e => e.Item.Id, StringComparer.Ordinal)
                    .ToList();
            }
            bumpers = Playable(clips.Bumpers);
            openers = bumpers.Where(b => ChannelTags.BumperSide(b.Item.Tags) != BumperPlacement.In).ToList();
            closers = bumpers.Where(b => ChannelTags.BumperSide(b.Item.Tags) != BumperPlacement.Out).ToList();
            promos = Playable(clips.Promos);
            commercials = Playable(clips.Commercials);
            trailers = Playable(clips.Trailers);
            interstitials = Playable(clips.Interstitials);
        }

        public bool IsEmpty => bumpers.Count == 0 && promos.Count == 0 && commercials.Count == 0 && trailers.Count == 0 && interstitials.Count == 0;

        /// <summary>For the checkpoint key: the clips and how breaks use them.</summary>
        public string Key(ChannelBreaks breaks)
        {
            var ids = string.Join(";", new[] { openers, closers, promos, commercials, trailers, interstitials }.Select(list => string.Join(",", list.Select(e => e.Item.Id))));
            return $"{breaks.BumpersAt.Tag}-{breaks.PromoCount}-{breaks.TrailerCount}-{breaks.InterstitialCount}-{breaks.Minutes}-{breaks.Every}-{breaks.Align}-{Seed(ids)}";
        }

        public sealed record Clip(BaseItem Item, ProgramRole Role, double Start, double End);

        private static readonly ProgramRole[] BreakOrder = [ProgramRole.Commercial, ProgramRole.Promo, ProgramRole.Trailer, ProgramRole.Interstitial, ProgramRole.Bumper];

        /// <summary>
        /// Whole clips of every kind the channel has (bumpers only those for
        /// either end) filling as much of <paramref name="seconds"/> as they
        /// can without going over: chosen at random while there's room for
        /// any, then the longest that still fits. Grouped as a break runs.
        /// </summary>
        private List<(Entry Entry, ProgramRole Role, double Length)> BestFill(double seconds, string? previous, Func<ulong> next)
        {
            var pool = commercials.Select(e => (Entry: e, Role: ProgramRole.Commercial))
                .Concat(promos.Select(e => (Entry: e, Role: ProgramRole.Promo)))
                .Concat(trailers.Select(e => (Entry: e, Role: ProgramRole.Trailer)))
                .Concat(interstitials.Select(e => (Entry: e, Role: ProgramRole.Interstitial)))
                .Concat(bumpers.Where(b => ChannelTags.BumperSide(b.Item.Tags) == BumperPlacement.Both).Select(e => (Entry: e, Role: ProgramRole.Bumper)))
                .ToList();
            if (pool.Count == 0) return [];
            var longest = pool.Max(p => p.Entry.Length);
            var chosen = new List<(Entry Entry, ProgramRole Role)>();
            var remaining = seconds;
            var last = previous;
            while (chosen.Count < 2000)
            {
                var fitting = pool.Where(p => p.Entry.Length <= remaining && p.Entry.Item.Id != last).ToList();
                if (fitting.Count == 0) fitting = pool.Where(p => p.Entry.Length <= remaining).ToList();
                if (fitting.Count == 0) break;
                (Entry Entry, ProgramRole Role) clip;
                if (remaining > longest)
                {
                    clip = fitting[(int)(next() % (ulong)fitting.Count)];
                }
                else
                {
                    // The longest; of equal lengths, the first with the smallest id (as Swift's max(by:) keeps).
                    clip = fitting[0];
                    foreach (var candidate in fitting.Skip(1))
                    {
                        var increasing = clip.Entry.Length < candidate.Entry.Length
                            || (clip.Entry.Length == candidate.Entry.Length && string.CompareOrdinal(candidate.Entry.Item.Id, clip.Entry.Item.Id) < 0);
                        if (increasing) clip = candidate;
                    }
                }
                chosen.Add(clip);
                remaining -= clip.Entry.Length;
                last = clip.Entry.Item.Id;
            }
            return chosen
                .Select((c, offset) => (c.Entry, c.Role, Offset: offset))
                .OrderBy(c => Array.IndexOf(BreakOrder, c.Role)).ThenBy(c => c.Offset)
                .Select(c => (c.Entry, c.Role, c.Entry.Length))
                .ToList();
        }

        /// <summary>
        /// The break after a programme ending at <paramref name="start"/>: a
        /// bumper out, commercials filling the minutes with whole clips,
        /// promos, trailers, then interstitials, and a bumper back in. Lined
        /// up on a mark, the rest of the way to it is filled: best effort
        /// with whole clips of every kind, or exactly, the last cut short.
        /// </summary>
        public List<Clip> Deal(double start, ChannelBreaks breaks, ulong seed, TimeZoneInfo zone)
        {
            var random = Mix(seed ^ 0x2545_F491_4F6C_DD1DUL ^ unchecked((ulong)(long)Math.Round(start + ScheduledProgram.Since1970, MidpointRounding.AwayFromZero)));
            ulong Next()
            {
                random = Mix(random);
                return random;
            }
            // One of the pool, not `avoiding` where there's a choice.
            Entry Pick(List<Entry> pool, string? avoiding)
            {
                var first = pool[(int)(Next() % (ulong)pool.Count)];
                if (pool.Count <= 1 || first.Item.Id != avoiding) return first;
                return pool[(pool.FindIndex(e => e.Item.Id == first.Item.Id) + 1) % pool.Count];
            }
            // Clips of the pool filling `seconds`: whole ones, the last cut
            // short to fill it exactly with `cutting`; without, at least one
            // even when none is short enough.
            List<(Entry Entry, ProgramRole Role, double Length)> Fill(List<Entry> pool, ProgramRole role, double seconds, bool cutting, string? previous)
            {
                var result = new List<(Entry, ProgramRole, double)>();
                var remaining = seconds;
                var shortest = pool.Count == 0 ? double.PositiveInfinity : pool.Min(e => e.Length);
                while (remaining >= ShortestClip && result.Count < 2000)
                {
                    var clip = Pick(pool, previous);
                    if (clip.Length > remaining && shortest <= remaining)
                    {
                        // Another that fits, where one does.
                        clip = pool.FirstOrDefault(e => e.Length <= remaining && e.Item.Id != previous) ?? pool.First(e => e.Length <= remaining);
                    }
                    if (clip.Length <= remaining)
                    {
                        result.Add((clip, role, clip.Length));
                        remaining -= clip.Length;
                    }
                    else if (cutting)
                    {
                        result.Add((clip, role, remaining));
                        remaining = 0;
                    }
                    else
                    {
                        if (result.Count == 0) result.Add((clip, role, clip.Length));
                        break;
                    }
                    previous = clip.Item.Id;
                }
                return result;
            }

            var parts = new List<(Entry Entry, ProgramRole Role, double Length)>();
            void Add(Entry entry, ProgramRole role) => parts.Add((entry, role, entry.Length));
            string? LastId() => parts.Count > 0 ? parts[^1].Entry.Item.Id : null;

            if (openers.Count > 0 && breaks.BumpersAt != BumperPlacement.In) Add(Pick(openers, null), ProgramRole.Bumper);
            if (commercials.Count > 0 && breaks.Minutes > 0) parts.AddRange(Fill(commercials, ProgramRole.Commercial, breaks.Minutes * 60, cutting: false, LastId()));
            if (promos.Count > 0)
            {
                for (var i = 0; i < breaks.PromoCount; i++) Add(Pick(promos, LastId()), ProgramRole.Promo);
            }
            // Whole, never cut short or used to fill to the mark.
            if (trailers.Count > 0)
            {
                for (var i = 0; i < breaks.TrailerCount; i++) Add(Pick(trailers, LastId()), ProgramRole.Trailer);
            }
            var closing = closers.Count > 0 && breaks.BumpersAt != BumperPlacement.Out ? Pick(closers, parts.Count > 0 ? parts[0].Entry.Item.Id : null) : null;

            double? mark = null;
            if (breaks.Align > 0 && !breaks.ExactAlign)
            {
                var used = parts.Aggregate(0.0, (sum, p) => sum + p.Length) + (closing?.Length ?? 0);
                var target = NextMark(start + used, breaks.Align, zone);
                parts.AddRange(BestFill(target - start - used, LastId(), Next));
            }
            else if (breaks.Align > 0)
            {
                var used = parts.Aggregate(0.0, (sum, p) => sum + p.Length) + (closing?.Length ?? 0);
                var target = NextMark(start + used, breaks.Align, zone);
                // Bumpers fill only when nothing else can, and never one meant for one end of a break.
                var (filler, role) = interstitials.Count > 0 ? (interstitials, ProgramRole.Interstitial)
                    : commercials.Count > 0 ? (commercials, ProgramRole.Commercial)
                    : promos.Count > 0 ? (promos, ProgramRole.Promo)
                    : (bumpers.Where(b => ChannelTags.BumperSide(b.Item.Tags) == BumperPlacement.Both).ToList(), ProgramRole.Bumper);
                if (filler.Count > 0)
                {
                    parts.AddRange(Fill(filler, role, target - start - used, cutting: true, LastId()));
                    mark = target;
                }
            }
            else if (interstitials.Count > 0)
            {
                for (var i = 0; i < breaks.InterstitialCount; i++) Add(Pick(interstitials, LastId()), ProgramRole.Interstitial);
            }
            if (closing is not null) Add(closing, ProgramRole.Bumper);

            var result = new List<Clip>();
            var time = start;
            foreach (var (entry, role, length) in parts)
            {
                var clipEnd = time + length;
                result.Add(new Clip(entry.Item, role, time, clipEnd));
                time = clipEnd;
            }
            // Exactly on the mark, whatever the sums came to.
            if (mark is { } exact && result.Count > 0 && Math.Abs(result[^1].End - exact) < 1)
            {
                result[^1] = result[^1] with { End = exact };
            }
            return result;
        }
    }

    // ---- The calendar, as Foundation's Gregorian one counts in the zone ----

    /// <summary>The first time at or after <paramref name="date"/> that's a multiple of <paramref name="minutes"/> past local midnight.</summary>
    private static double NextMark(double date, int minutes, TimeZoneInfo zone)
    {
        var midnight = StartOfDay(date, zone);
        double step = minutes * 60;
        var steps = Math.Ceiling((date - midnight) / step);
        return midnight + steps * step;
    }

    /// <summary>When the period starting at <paramref name="start"/> ends: the next midnight, the next Monday's, or the next 1st's.</summary>
    private static double EndOfPeriod(double start, ChannelReshuffle reshuffle, TimeZoneInfo zone)
    {
        var local = ToLocal(start, zone);
        switch (reshuffle)
        {
            case ChannelReshuffle.Week:
            {
                var day = local.Date.AddDays(1);
                while (day.DayOfWeek != DayOfWeek.Monday) day = day.AddDays(1);
                return FromLocal(day, zone);
            }
            case ChannelReshuffle.Month:
                return FromLocal(new DateTime(local.Year, local.Month, 1).AddMonths(1), zone);
            default:
                return FromLocal(local.AddDays(1), zone);
        }
    }

    /// <summary>Local midnight of the day <paramref name="date"/> falls in.</summary>
    internal static double StartOfDay(double date, TimeZoneInfo zone) => FromLocal(ToLocal(date, zone).Date, zone);

    private static DateTime ToLocal(double seconds, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(ScheduledProgram.ToDate(seconds).UtcDateTime, zone);

    /// <summary>
    /// A wall-clock time in the zone as seconds since the reference date.
    /// A time skipped by daylight saving moves on to the first that exists;
    /// one that happens twice is the first of them, as Foundation's are.
    /// </summary>
    private static double FromLocal(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(15);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return ScheduledProgram.ToSeconds(new DateTimeOffset(local, offset));
    }

    /// <summary>The round with each show as many times as its turns, shuffled again and spread so a show never follows itself where that can be helped.</summary>
    private static int[] Weighted(int[] order, int[] turns, ulong seed)
    {
        var round = Shuffled(order.SelectMany(i => Enumerable.Repeat(i, turns[i])).ToArray(), Mix(seed ^ 0xA076_1D64_78BD_642FUL));
        if (round.Length <= 2) return round;
        for (var index = 1; index < round.Length; index++)
        {
            if (round[index] != round[index - 1]) continue;
            for (var swap = index + 1; swap < round.Length; swap++)
            {
                if (round[swap] == round[index - 1]) continue;
                (round[index], round[swap]) = (round[swap], round[index]);
                break;
            }
        }
        return round;
    }

    // ---- Shuffling the same way everywhere ----

    /// <summary>FNV-1a of the text's UTF-8: a seed that doesn't change between launches or devices.</summary>
    internal static ulong Seed(string text)
    {
        var hash = 0xCBF2_9CE4_8422_2325UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
        {
            hash ^= b;
            hash = unchecked(hash * 0x0000_0100_0000_01B3UL);
        }
        return hash;
    }

    /// <summary>SplitMix64's output step: a well-mixed number from any seed.</summary>
    internal static ulong Mix(ulong value)
    {
        unchecked
        {
            var z = value + 0x9E37_79B9_7F4A_7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
            return z ^ (z >> 31);
        }
    }

    /// <summary>Fisher–Yates with SplitMix64, written out so the order is the same everywhere.</summary>
    private static T[] Shuffled<T>(T[] items, ulong seed)
    {
        var result = (T[])items.Clone();
        var state = seed;
        ulong Next()
        {
            unchecked
            {
                state += 0x9E37_79B9_7F4A_7C15UL;
                var z = state;
                z = (z ^ (z >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D0_49BB_1331_11EBUL;
                return z ^ (z >> 31);
            }
        }
        if (result.Length <= 1) return result;
        for (var i = result.Length - 1; i > 0; i--)
        {
            var j = (int)(Next() % (ulong)(i + 1));
            (result[i], result[j]) = (result[j], result[i]);
        }
        return result;
    }
}
