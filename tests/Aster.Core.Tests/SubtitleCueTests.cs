using System.Text;
using Aster.Core.Playback;

namespace Aster.Core.Tests;

public class SubtitleCueTests
{
    [Fact]
    public void PlainTextRemovesMarkupAndKeepsLines()
    {
        Assert.Equal("Hello\nworld & co", SubtitleCue.PlainText("{\\an8}<i>Hello</i><br/> world &amp; co "));
        Assert.Equal("one\ntwo", SubtitleCue.PlainText("one\\Ntwo"));
        Assert.Equal("&lt;", SubtitleCue.PlainText("&amp;lt;"));
    }

    [Fact]
    public void DrawingsAreLeftOutUntilTurnedOff()
    {
        Assert.Equal("Sign", SubtitleCue.PlainText("{\\p1}m 0 0 l 100 0 100 50{\\p0}Sign"));
        Assert.Equal("Kept", SubtitleCue.PlainText("{\\pos(10,10)}Kept"));
        Assert.Equal("", SubtitleCue.PlainText("{\\p2}m 0 0 l 1 1"));
    }

    [Fact]
    public void ParseReadsTicksAndSortsByStart()
    {
        var json = """
            {"TrackEvents":[
              {"Text":"second","StartPositionTicks":30000000,"EndPositionTicks":40000000},
              {"Text":"<b></b>","StartPositionTicks":15000000,"EndPositionTicks":16000000},
              {"Text":"first","StartPositionTicks":10000000,"EndPositionTicks":35000000}]}
            """;
        var cues = SubtitleCue.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["first", "second"], cues.Select(c => c.Text));
        Assert.Equal(1.0, cues[0].Start);
    }

    [Fact]
    public void TextAtShowsEveryOverlappingCue()
    {
        IReadOnlyList<SubtitleCue> cues = [new(1, 3.5, "first"), new(3, 4, "second"), new(10, 11, "later")];
        Assert.Null(SubtitleCue.TextAt(0.5, cues));
        Assert.Equal("first", SubtitleCue.TextAt(2, cues));
        Assert.Equal("first\nsecond", SubtitleCue.TextAt(3.2, cues));
        Assert.Null(SubtitleCue.TextAt(5, cues));
        Assert.Null(SubtitleCue.TextAt(1, []));
    }
}
