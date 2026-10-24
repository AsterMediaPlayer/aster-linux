using Aster.Core.DiscMenu;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>A menu's pictures, sounds and button tones.</summary>
public class DiscMenuAssetsTests
{
    private const string MovieId = "0e97d452f45b4be795db7620ecc3662a";
    private static readonly DiscPictureResolver Resolver = new(new JellyfinClient(new Uri("https://jellyfin.example/base/"), "token", "user"), MovieId);

    [Fact]
    public void AssetsComeFromTheServerAndAnythingUnsafeIsIgnored()
    {
        Assert.Equal("https://jellyfin.example/base/DiscMenus/Assets/thor/banner.webp", Resolver.Picture("asset:thor/banner.webp")!.AbsoluteUri);
        Assert.Equal("https://cdn.example/a.png", Resolver.Picture("https://cdn.example/a.png")!.AbsoluteUri);
        Assert.Null(Resolver.Picture("http://cdn.example/a.png"));
        Assert.Null(Resolver.Picture("asset:../secret"));
        Assert.Null(Resolver.Audio("asset:menu/song.exe"));
        Assert.Equal("https://jellyfin.example/base/DiscMenus/Assets/menu/song.ogg", Resolver.Audio("asset:menu/song.ogg")!.AbsoluteUri);
    }

    [Fact]
    public void BackgroundsNameTheirPicture()
    {
        Assert.Equal($"https://jellyfin.example/base/Items/{MovieId}/Images/Backdrop/2?maxWidth=1920&quality=90",
            Resolver.Background(new DiscBackground { Source = "jellyfin", Index = 2 })!.AbsoluteUri);
        Assert.Equal($"https://jellyfin.example/base/Items/{MovieId}/Images/Primary/0?maxWidth=1920&quality=90",
            Resolver.Background(new DiscBackground { Source = "jellyfin", ImageType = "Primary" })!.AbsoluteUri);
        Assert.Equal("https://image.tmdb.org/t/p/w1280/abc.jpg",
            Resolver.Background(new DiscBackground { Source = "tmdb", TmdbFilePath = "/abc.jpg", TmdbSize = "huge" })!.AbsoluteUri);
        Assert.Null(Resolver.Background(new DiscBackground { Source = "tmdb", TmdbFilePath = "/../abc.jpg" }));
        Assert.Equal($"https://jellyfin.example/base/DiscMenus/Fanart/{MovieId}/123",
            Resolver.Background(new DiscBackground { Source = "fanart", FanartId = "123" })!.AbsoluteUri);
        Assert.Null(Resolver.Background(new DiscBackground { Source = "fanart", FanartId = "12a" }));
        Assert.Null(Resolver.Background(new DiscBackground { Source = "color", Color = "#000000" }));
    }

    [Fact]
    public void TonesArePresetsShapedAsTheReferenceShapesThem()
    {
        Assert.Null(DiscTones.Render("none", DiscSound.Move, 0.5));
        Assert.Null(DiscTones.Render("kazoo", DiscSound.Move, 0.5));
        // Click's select is two tones, 40 ms and 50 ms, the second starting as the first ends.
        var click = DiscTones.Render("click", DiscSound.Select, 0.5)!;
        Assert.Equal((int)(0.09 * DiscTones.SampleRate) + 64, click.Length);
        // Never louder than its peak: volume × gain × 0.3.
        var beep = DiscTones.Render("beep", DiscSound.Select, 1)!;
        Assert.InRange(beep.Max(Math.Abs), 0.1, 0.6 * 0.3 + 1e-6);
        // Silent before and after.
        Assert.True(Math.Abs(beep[0]) < 0.001);
        Assert.Equal(0, beep[^1]);
    }
}
