using Aster.Core.Channels;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>
/// The Apple app's ChannelScheduleTests: schedules the same on every run,
/// back to back with no gaps, tuning in on the right spot, a movie at
/// midnight running on into the next day, each day its own order; TV shows
/// in order across days, taking turns; breaks. One test holds a lineup the
/// Apple app worked out, so the two can't deal differently.
/// </summary>
public class ChannelScheduleTests
{
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    private static BaseItem Movie(string id, double? minutes) => new()
    {
        Id = id, Name = $"Movie {id}", Type = "Movie", RunTimeTicks = minutes is { } m ? (long)(m * 60 * 10_000_000) : null,
    };

    /// <summary>Eight movies of different lengths, 85 to 155 minutes.</summary>
    private static readonly BaseItem[] Movies = Enumerable.Range(0, 8).Select(i => Movie($"m{i}", 85 + i * 10.0)).ToArray();

    private static DateTimeOffset Date(int year, int month, int day, int hour = 0, int minute = 0)
    {
        var local = new DateTime(year, month, day, hour, minute, 0);
        return new DateTimeOffset(local, Central.GetUtcOffset(local));
    }

    private static IReadOnlyList<ScheduledProgram> Programs(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<BaseItem>? movies = null, string channel = "channel-a", ChannelLineup? lineup = null) =>
        ChannelSchedule.Programs(channel, movies ?? Movies, lineup ?? new ChannelLineup(), from, to, Central);

    private static BaseItem Episode(string series, int season, int number, double minutes = 22) => new()
    {
        Id = $"{series}-s{season}e{number}", Name = $"{series} {season}x{number}", Type = "Episode", SeriesId = series,
        SeriesName = char.ToUpperInvariant(series[0]) + series[1..], ParentIndexNumber = season, IndexNumber = number,
        RunTimeTicks = (long)(minutes * 60 * 10_000_000),
    };

    /// <summary>Three shows of two seasons each, listed out of order, with a special.</summary>
    private static readonly BaseItem[] Shows = BuildShows();

    private static BaseItem[] BuildShows()
    {
        var items = new List<BaseItem>();
        foreach (var series in new[] { "alpha", "bravo", "charlie" })
        {
            for (var season = 1; season <= 2; season++)
                for (var number = 1; number <= 6; number++) items.Add(Episode(series, season, number));
            items.Add(Episode(series, 0, 1));
        }
        items.Reverse();
        return [.. items];
    }

    private static IReadOnlyList<ScheduledProgram> Tv(ChannelMode mode, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<BaseItem>? items = null, string channel = "tv") =>
        ChannelSchedule.Programs(channel, items ?? Shows, new ChannelLineup { Mode = mode }, from, to, Central);

    private static int Position(BaseItem item) => ((item.ParentIndexNumber ?? 1) - 1) * 6 + (item.IndexNumber ?? 1) - 1;

    /// <summary>
    /// A channel without the newer tags keeps the schedule it had, across the
    /// end of daylight saving time: the lineups the Apple app's tests hold.
    /// </summary>
    [Fact]
    public void UntaggedChannelsKeepTheAppleAppsSchedule()
    {
        var movies = Programs(Date(2026, 10, 31, 18), Date(2026, 11, 2, 6)).Select(p => p.Item.Id);
        Assert.Equal(["m1", "m3", "m0", "m2", "m1", "m3", "m0", "m7", "m5", "m4", "m2", "m6", "m1", "m3", "m0", "m7", "m5", "m0", "m5", "m3"], movies);

        var series = new[] { "s0", "s1", "s2" }.SelectMany(name => Enumerable.Range(1, 6).Select(n => Episode(name, 1, n))).ToArray();
        var blocks = Tv(ChannelMode.Blocks, Date(2026, 10, 31, 18), Date(2026, 11, 1, 6), series, "channel-b").Select(p => p.Item.Id);
        Assert.Equal([
            "s2-s1e4", "s1-s1e5", "s1-s1e6", "s0-s1e1", "s0-s1e2", "s0-s1e3", "s2-s1e5", "s2-s1e6",
            "s1-s1e1", "s1-s1e2", "s0-s1e4", "s0-s1e5", "s0-s1e6", "s0-s1e1", "s2-s1e1", "s2-s1e2",
            "s2-s1e3", "s1-s1e3", "s1-s1e4", "s1-s1e5", "s1-s1e6", "s2-s1e4", "s2-s1e5", "s0-s1e2",
            "s0-s1e3", "s0-s1e4", "s1-s1e1", "s1-s1e2", "s1-s1e3", "s2-s1e6", "s2-s1e1", "s0-s1e5",
            "s0-s1e6", "s0-s1e1", "s1-s1e4", "s1-s1e5",
        ], blocks);
    }

    [Fact]
    public void ListOrderDoesNotMatter()
    {
        var (from, to) = (Date(2026, 10, 15, 18), Date(2026, 10, 16, 6));
        Assert.Equal(Programs(from, to).Select(p => p.Item.Id), Programs(from, to, Movies.Reverse().ToArray()).Select(p => p.Item.Id));
    }

    [Fact]
    public void ProgramsRunBackToBackAcrossTheWindow()
    {
        var (from, to) = (Date(2026, 10, 15, 8), Date(2026, 10, 17, 8));
        var programs = Programs(from, to);
        Assert.True(programs[0].Start <= from);
        Assert.True(programs[^1].End >= to);
        foreach (var (earlier, later) in programs.Zip(programs.Skip(1))) Assert.Equal(earlier.EndSeconds, later.StartSeconds);
    }

    [Fact]
    public void TuningInFindsTheMovieAndTheSpot()
    {
        var now = Date(2026, 10, 15, 21, 17);
        var airing = ChannelSchedule.Airing("channel-a", Movies, new ChannelLineup(), now, Central);
        Assert.NotNull(airing);
        Assert.True(airing.Contains(now));
        var window = Programs(now.AddHours(-6), now.AddHours(6));
        Assert.Equal(airing.Id, window.First(p => p.Contains(now)).Id);
    }

    [Fact]
    public void AMovieAtMidnightRunsIntoTheNextDay()
    {
        var midnight = Date(2026, 10, 16);
        var programs = Programs(midnight.AddHours(-4), midnight.AddHours(4));
        var crossing = programs.First(p => p.Start < midnight && p.End > midnight);
        var next = programs.First(p => p.StartSeconds == crossing.EndSeconds);
        Assert.NotEqual(crossing.Item.Id, next.Item.Id);
    }

    [Fact]
    public void OtherChannelsShuffleDifferently()
    {
        var (from, to) = (Date(2026, 10, 15, 6), Date(2026, 10, 15, 22));
        Assert.NotEqual(Programs(from, to).Select(p => p.Item.Id), Programs(from, to, channel: "channel-b").Select(p => p.Item.Id));
    }

    [Fact]
    public void MoviesWithoutARuntimeAreLeftOut()
    {
        var programs = Programs(Date(2026, 10, 15), Date(2026, 10, 16), [Movie("short", 0.5), Movie("none", null), Movie("real", 100)]);
        Assert.NotEmpty(programs);
        Assert.All(programs, p => Assert.Equal("real", p.Item.Id));
    }

    [Theory]
    [InlineData(ChannelMode.Rotation)]
    [InlineData(ChannelMode.Blocks)]
    public void EpisodesComeInOrderAcrossDaysWithoutSpecials(ChannelMode mode)
    {
        var programs = Tv(mode, Date(2026, 10, 15), Date(2026, 10, 18));
        Assert.All(programs, p => Assert.NotEqual(0, p.Item.ParentIndexNumber));
        foreach (var series in new[] { "alpha", "bravo", "charlie" })
        {
            var positions = programs.Where(p => p.Item.SeriesId == series).Select(p => Position(p.Item)).ToList();
            Assert.True(positions.Count > 12);
            foreach (var (earlier, later) in positions.Zip(positions.Skip(1))) Assert.Equal((earlier + 1) % 12, later);
        }
    }

    [Fact]
    public void RotationNeverRepeatsAShowBackToBack()
    {
        var programs = Tv(ChannelMode.Rotation, Date(2026, 10, 15), Date(2026, 10, 18));
        foreach (var (earlier, later) in programs.Zip(programs.Skip(1))) Assert.NotEqual(earlier.Item.SeriesId, later.Item.SeriesId);
    }

    [Fact]
    public void ResumingFromACheckpointMatchesAFullWalk()
    {
        var (from, to) = (Date(2027, 6, 1, 20), Date(2027, 6, 2, 2));
        var first = Tv(ChannelMode.Blocks, from, to, channel: "checkpoint");
        var second = Tv(ChannelMode.Blocks, from, to, channel: "checkpoint");
        Assert.NotEmpty(first);
        Assert.Equal(first.Select(p => p.Id), second.Select(p => p.Id));
    }

    [Fact]
    public void InOrderFollowsThePlaylistAndWraps()
    {
        var order = Shows.Select(s => s.Id).ToList();
        var programs = Tv(ChannelMode.Sequential, Date(2026, 10, 15), Date(2026, 10, 18));
        Assert.True(programs.Count > order.Count);
        foreach (var (earlier, later) in programs.Zip(programs.Skip(1)))
            Assert.Equal(order[(order.IndexOf(earlier.Item.Id) + 1) % order.Count], later.Item.Id);
    }

    [Theory]
    [InlineData(ChannelReshuffle.Week, 13, 18)]
    [InlineData(ChannelReshuffle.Month, 2, 30)]
    public void AWeekOrMonthKeepsOneOrder(ChannelReshuffle reshuffle, int firstDay, int lastDay)
    {
        var ids = Programs(Date(2026, 10, firstDay), Date(2026, 10, lastDay), lineup: new ChannelLineup { Reshuffle = reshuffle }).Select(p => p.Item.Id).ToList();
        Assert.True(ids.Count > 30);
        for (var index = 0; index < ids.Count - 8; index++) Assert.Equal(ids[index], ids[index + 8]);
    }

    [Fact]
    public void ASeedDealsAFreshLineup()
    {
        var (from, to) = (Date(2026, 10, 15, 18), Date(2026, 10, 16, 6));
        var original = Programs(from, to).Select(p => p.Item.Id).ToList();
        var reshuffled = Programs(from, to, lineup: new ChannelLineup { Seed = 1 }).Select(p => p.Item.Id).ToList();
        Assert.NotEqual(original, reshuffled);
        Assert.NotEqual(reshuffled, Programs(from, to, lineup: new ChannelLineup { Seed = 2 }).Select(p => p.Item.Id));
    }

    [Fact]
    public void LengthLimitsLeaveTitlesOut()
    {
        var programs = Programs(Date(2026, 10, 15), Date(2026, 10, 16), lineup: new ChannelLineup { MinLength = 100 * 60, MaxLength = 130 * 60 });
        Assert.Equal(["m2", "m3", "m4"], programs.Select(p => p.Item.Id).Distinct().Order());
    }

    // ---- Breaks ----

    private static BaseItem Clip(string id, double seconds, params string[] tags) => new()
    {
        Id = id, Name = id, Type = "Video", RunTimeTicks = (long)(seconds * 10_000_000), Tags = tags,
    };

    /// <summary>Three 5-second bumpers, two 15-second promos, four 30-second commercials and three 20-second interstitials.</summary>
    private static readonly ChannelBreakClips Clips = new()
    {
        Bumpers = Enumerable.Range(0, 3).Select(i => Clip($"bumper{i}", 5)).ToList(),
        Promos = Enumerable.Range(0, 2).Select(i => Clip($"promo{i}", 15)).ToList(),
        Commercials = Enumerable.Range(0, 4).Select(i => Clip($"ad{i}", 30)).ToList(),
        Interstitials = Enumerable.Range(0, 3).Select(i => Clip($"short{i}", 20)).ToList(),
    };

    private static IReadOnlyList<ScheduledProgram> Segments(ChannelLineup lineup, DateTimeOffset from, DateTimeOffset to, ChannelBreakClips? clips = null) =>
        ChannelSchedule.Segments("channel-a", Movies, lineup, clips ?? Clips, from, to, Central);

    private static List<List<ProgramRole>> Breaks(IReadOnlyList<ScheduledProgram> segments)
    {
        var breaks = new List<List<ProgramRole>>();
        List<ProgramRole>? current = null;
        foreach (var segment in segments)
        {
            if (segment.Role == ProgramRole.Programme)
            {
                if (current is not null) breaks.Add(current);
                current = [];
            }
            else current?.Add(segment.Role);
        }
        return breaks;
    }

    [Fact]
    public void ABreakFollowsEachProgramme()
    {
        var segments = Segments(new ChannelLineup(), Date(2026, 10, 15, 8), Date(2026, 10, 15, 20));
        var breaks = Breaks(segments);
        Assert.True(breaks.Count > 3);
        ProgramRole[] expected = [ProgramRole.Bumper, ProgramRole.Commercial, ProgramRole.Commercial, ProgramRole.Commercial, ProgramRole.Commercial, ProgramRole.Promo, ProgramRole.Interstitial, ProgramRole.Bumper];
        Assert.All(breaks, b => Assert.Equal(expected, b));
        foreach (var (a, b) in segments.Zip(segments.Skip(1))) Assert.Equal(a.EndSeconds, b.StartSeconds);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(15)]
    [InlineData(60)]
    public void LinedUpProgrammesStartOnTheMark(int minutes)
    {
        var segments = Segments(new ChannelLineup { Breaks = new ChannelBreaks { Align = minutes, ExactAlign = true } }, Date(2026, 10, 15, 8), Date(2026, 10, 16, 8));
        var programmes = segments.Where(s => s.Role == ProgramRole.Programme).ToList();
        Assert.True(programmes.Count > 5);
        foreach (var programme in programmes.Skip(1))
        {
            var local = TimeZoneInfo.ConvertTime(programme.Start, Central);
            Assert.True(local.Minute % minutes == 0 && local.Second == 0 && local.Millisecond == 0, $"{local}");
        }
        foreach (var breakRoles in Breaks(segments))
        {
            Assert.Equal(ProgramRole.Bumper, breakRoles[0]);
            Assert.Equal(ProgramRole.Bumper, breakRoles[^1]);
            Assert.Contains(ProgramRole.Interstitial, breakRoles);
        }
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public void BestEffortLandsJustBeforeTheMarkWithWholeClips(int minutes)
    {
        var clips = Clips with { Trailers = Enumerable.Range(0, 2).Select(i => Clip($"trailer{i}", 150)).ToList() };
        var segments = Segments(new ChannelLineup { Breaks = new ChannelBreaks { Align = minutes } }, Date(2026, 10, 15, 8), Date(2026, 10, 16, 8), clips);
        var step = minutes * 60.0;
        foreach (var programme in segments.Where(s => s.Role == ProgramRole.Programme).Skip(1))
        {
            var midnight = ChannelSchedule.StartOfDay(programme.StartSeconds, Central);
            var into = programme.StartSeconds - midnight;
            var early = Math.Ceiling(into / step) * step - into;
            Assert.True(early is >= 0 and < 5, $"{early}");
        }
        foreach (var segment in segments.Where(s => s.Role.IsBreak))
            Assert.True(Math.Abs(segment.Duration - segment.Item.RunTimeTicks!.Value / 10_000_000.0) < 0.001);
    }

    [Fact]
    public void TheGuideFoldsEachBreakIntoItsProgramme()
    {
        var (from, to) = (Date(2026, 10, 15, 8), Date(2026, 10, 15, 20));
        var lineup = new ChannelLineup { Breaks = new ChannelBreaks { Align = 30 } };
        var guide = ChannelSchedule.Programs("channel-a", Movies, lineup, Clips, from, to, Central);
        Assert.All(guide, p => Assert.Equal(ProgramRole.Programme, p.Role));
        foreach (var (a, b) in guide.Zip(guide.Skip(1))) Assert.Equal(a.EndSeconds, b.StartSeconds);
        Assert.True(guide[0].Start <= from && guide[0].End > from);
    }

    [Fact]
    public void ABumperTaggedForOneEndStaysThere()
    {
        var countdown = Clip("countdown", 10, "aster:bumper=in");
        var alone = Segments(new ChannelLineup(), Date(2026, 10, 15, 8), Date(2026, 10, 15, 20), new ChannelBreakClips { Bumpers = [countdown] });
        var breaks = Breaks(alone);
        Assert.NotEmpty(breaks);
        Assert.All(breaks, b => Assert.Equal([ProgramRole.Bumper], b));
    }
}
