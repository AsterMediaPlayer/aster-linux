namespace Aster.Core.Tests;

/// <summary>The subtitle style chosen in Settings, as mpv's options.</summary>
public class SubtitleStyleTests
{
    [Fact]
    public void NormalIsMpvsOwn()
    {
        var options = SubtitleStyle.MpvOptions(SubtitleSize.Normal, SubtitleColor.White, SubtitleBackground.Outline);
        Assert.Equal("1", options["sub-scale"]);
        Assert.Equal("#FFFFFFFF", options["sub-color"]);
        Assert.False(options.ContainsKey("sub-border-style"));
    }

    [Fact]
    public void LargeYellowOnABox()
    {
        var options = SubtitleStyle.MpvOptions(SubtitleSize.ExtraLarge, SubtitleColor.Yellow, SubtitleBackground.Box);
        Assert.Equal("1.5", options["sub-scale"]);
        Assert.Equal("#FFFFE14D", options["sub-color"]);
        Assert.Equal("background-box", options["sub-border-style"]);
    }

    [Fact]
    public void SavedSettingsWithoutItAreNormal() => Assert.Equal(SubtitleSize.Normal, new AppSettings().SubtitleSize);
}
