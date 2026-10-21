using System.Globalization;
using Aster.Core.Jellyfin;
using Aster.Core.LiveTV;

namespace Aster.Core.Tests;

public class LiveTVTests
{
    private static readonly TimeZoneInfo Central = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private static BaseItem Channel(string id, string? number, bool favorite = false) => new()
    {
        Id = id, Name = $"Channel {id}", Type = "TvChannel", ChannelNumber = number,
        UserData = new UserItemData { IsFavorite = favorite },
    };

    private static BaseItem Program(string? start, string? end) => new() { Id = "p", Type = "Program", StartDate = start, EndDate = end };

    [Fact]
    public void GuideOrderPutsFavouritesFirstThenNumbers()
    {
        var order = LiveGuide.Order([Channel("a", "12"), Channel("b", "2.1"), Channel("c", null), Channel("d", "40", favorite: true), Channel("e", "2")]);
        Assert.Equal(["d", "e", "b", "a", "c"], order.Select(c => c.Id));
    }

    [Fact]
    public void FloorsToTheHalfHour()
    {
        var date = new DateTimeOffset(2026, 10, 21, 19, 47, 12, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 19, 30, 0, TimeSpan.Zero), LiveGuide.FloorToHalfHour(date));
    }

    [Fact]
    public void WindowStaysInsideTheGuide()
    {
        var start = new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
        var end = start.AddHours(24);
        Assert.Equal(start, LiveGuide.Clamp(start.AddHours(-2), 120, start, end));
        Assert.Equal(end.AddHours(-2), LiveGuide.Clamp(end, 120, start, end));
        Assert.Equal(start.AddHours(3), LiveGuide.Clamp(start.AddHours(3), 120, start, end));
    }

    [Fact]
    public void SlotIsClippedAndNeverTooShort()
    {
        var guide = new DateTimeOffset(2026, 10, 21, 18, 0, 0, TimeSpan.Zero);
        var (start, end) = LiveGuide.Slot(Program("2026-10-21T17:30:00Z", "2026-10-21T18:30:00Z"), guide);
        Assert.Equal(guide, start);
        Assert.Equal(guide.AddMinutes(30), end);
        var (_, shortEnd) = LiveGuide.Slot(Program("2026-10-21T19:00:00Z", "2026-10-21T19:01:00Z"), guide);
        Assert.Equal(guide.AddHours(1).AddMinutes(5), shortEnd);
    }

    [Fact]
    public void AiringReadsInTheViewersTimeZone()
    {
        var now = new DateTimeOffset(2026, 10, 21, 17, 0, 0, TimeSpan.Zero); // noon in Chicago
        var start = new DateTimeOffset(2026, 10, 22, 0, 30, 0, TimeSpan.Zero); // 7:30 PM
        Assert.Equal("Today, 7:30 PM – 8:00 PM", LiveTVFormat.Airing(start, start.AddMinutes(30), now, Central, English));
        Assert.Equal("Tomorrow", LiveTVFormat.DayLabel(start.AddDays(1), now, Central, English));
        Assert.Equal("Friday, Oct 23", LiveTVFormat.DayLabel(start.AddDays(2), now, Central, English));
    }
}
