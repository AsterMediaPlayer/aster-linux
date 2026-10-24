using Aster.Core.Channels;

namespace Aster.Core.Tests;

/// <summary>
/// Channel groups: each in order of its lowest number, its channels by
/// number, the ungrouped under "Other", and nothing at all until some
/// channel has a group. The Apple app's ChannelGroupingTests.
/// </summary>
public class ChannelGroupingTests
{
    /// <summary>Channels as "number" or "number:group", in number order.</summary>
    private static (ChannelGrouping Grouping, IReadOnlyList<string> Ordered)? Make(params string[] channels) =>
        ChannelGrouping.Make(channels, c => c, c => c.Split(':', 2) is [_, var group] ? group : null);

    [Fact]
    public void NoGroupsMeansNoGrouping() => Assert.Null(Make("1", "2", "3"));

    [Fact]
    public void GroupsGoInOrderOfTheirLowestNumber()
    {
        var made = Make("1:Kids", "2:Anime", "3:Kids", "4:Movies", "5:Anime")!.Value;
        Assert.Equal(["Kids", "Anime", "Movies"], made.Grouping.Names);
        Assert.Equal(["1:Kids", "3:Kids", "2:Anime", "5:Anime", "4:Movies"], made.Ordered);
    }

    [Fact]
    public void UngroupedChannelsAreOtherRankedLikeAnyGroup()
    {
        var made = Make("1", "2:Kids", "3", "4:Anime")!.Value;
        Assert.Equal(["Other", "Kids", "Anime"], made.Grouping.Names);
        Assert.Equal(["1", "3", "2:Kids", "4:Anime"], made.Ordered);
        Assert.Equal(ChannelGrouping.Other, made.Grouping.GroupOf("3"));
    }

    [Fact]
    public void AGroupsChannelsKeepTheirNumberOrder()
    {
        var made = Make("200:Kids", "201:Kids", "300:Anime", "301:Anime", "202:Kids")!.Value;
        Assert.Equal(["200:Kids", "201:Kids", "202:Kids", "300:Anime", "301:Anime"], made.Ordered);
        Assert.Equal(["200:Kids", "201:Kids", "202:Kids"], made.Grouping.Channels(made.Ordered, "Kids"));
    }

    [Fact]
    public void TheGroupTagIsReadAndWritten()
    {
        var tags = ChannelTags.Parse(["aster:channel", "aster:group=  Late Night  "]);
        Assert.Equal("Late Night", tags.Group);
        Assert.Equal("aster:group=Late Night", tags.AppliedTo(["aster:channel"]).Last());
        Assert.Null(ChannelTags.Parse(["aster:channel", "aster:group="]).Group);
    }
}
