using System.Globalization;
using Aster.Core.Browsing;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>
/// The Upcoming tab's dates and wording: air dates (local midnight sent in
/// UTC) land on the right day, days are grouped in order, and the countdown
/// and episode lines read naturally. The Apple app's UpcomingTests.
/// </summary>
public class UpcomingTests
{
    /// <summary>Central time, where the server's dates were checked (05:00Z is midnight).</summary>
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    private static BaseItem Episode(string id, string series, int season, int number, string name, string airs, string location = "Virtual") => new()
    {
        Id = id, Name = name, Type = "Episode", SeriesName = series, SeriesId = $"series-{series}",
        ParentIndexNumber = season, IndexNumber = number, PremiereDate = airs, LocationType = location,
    };

    private static DateTime Day(string text) => DateTime.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    [Fact]
    public void AirDateReadsJellyfinsSevenDecimals()
    {
        var item = Episode("a", "Blue Box", 2, 3, "Episode 3", "2026-10-18T05:00:00.0000000Z");
        Assert.Equal(Day("2026-10-18"), UpcomingSchedule.AirDate(item, Central)?.Date);
    }

    [Fact]
    public void GroupsByLocalDayInOrder()
    {
        BaseItem[] items =
        [
            Episode("c", "Blue Box", 2, 3, "Episode 3", "2026-10-18T05:00:00.0000000Z"),
            Episode("b", "Reincarnated as a Sword", 2, 3, "Episode 3", "2026-10-15T05:00:00.0000000Z"),
            Episode("a", "Reincarnated as a Sword", 2, 2, "Jean du Vix Knows His Stuff", "2026-10-15T05:00:00.0000000Z"),
            Episode("d", "Ranma1/2 (2024)", 3, 3, "Episode 3", "2026-10-18T05:00:00.0000000Z"),
        ];
        var days = UpcomingSchedule.Days(items, Central);
        Assert.Equal([Day("2026-10-15"), Day("2026-10-18")], days.Select(d => d.Day));
        Assert.Equal(["a", "b"], days[0].Episodes.Select(e => e.Id));
        Assert.Equal(["c", "d"], days[1].Episodes.Select(e => e.Id));
    }

    [Fact]
    public void CountdownWording()
    {
        var now = Day("2026-10-13").AddHours(10);
        Assert.Equal("Today", UpcomingSchedule.Countdown(Day("2026-10-13"), now));
        Assert.Equal("Tomorrow", UpcomingSchedule.Countdown(Day("2026-10-14"), now));
        Assert.Equal("In 2 days", UpcomingSchedule.Countdown(Day("2026-10-15"), now));
        Assert.Equal("In 1 week", UpcomingSchedule.Countdown(Day("2026-10-20"), now));
        Assert.Equal("In 2 weeks", UpcomingSchedule.Countdown(Day("2026-10-27"), now));
    }

    [Fact]
    public void HeadingNamesTodayAndTomorrow()
    {
        var now = Day("2026-10-13").AddHours(10);
        var english = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal("Tomorrow · Wednesday, October 14", UpcomingSchedule.Heading(Day("2026-10-14"), now, english));
        Assert.Equal("Friday, October 16", UpcomingSchedule.Heading(Day("2026-10-16"), now, english));
    }

    [Fact]
    public void EpisodeLineDropsPlaceholderTitles()
    {
        Assert.Equal("S2 · E3", UpcomingSchedule.EpisodeLine(Episode("a", "Blue Box", 2, 3, "Episode 3", "")));
        Assert.Equal("S3 · E9 · The Blade", UpcomingSchedule.EpisodeLine(Episode("b", "Bleach", 3, 9, "The Blade", "")));
    }

    [Fact]
    public void AvailableEpisodesOpenThemselvesOthersTheirShow()
    {
        var early = Episode("a", "Sword", 2, 2, "Jean", "", location: "FileSystem");
        var placeholder = Episode("b", "Sword", 2, 3, "Episode 3", "");
        Assert.Equal("a", UpcomingSchedule.Destination(early).Id);
        Assert.Equal("series-Sword", UpcomingSchedule.Destination(placeholder).Id);
        Assert.Equal(ItemKind.Series, UpcomingSchedule.Destination(placeholder).Kind);
    }
}
