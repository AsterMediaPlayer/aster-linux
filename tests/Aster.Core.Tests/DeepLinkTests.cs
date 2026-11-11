namespace Aster.Core.Tests;

/// <summary>Links into the app, as the Apple app's DeepLinkRouter reads them.</summary>
public class DeepLinkTests
{
    [Theory]
    [InlineData("aster://item/abc123", DeepLinkKind.Item, "abc123")]
    [InlineData("aster://play/abc123", DeepLinkKind.Play, "abc123")]
    [InlineData("aster://channel/ch-7", DeepLinkKind.Channel, "ch-7")]
    [InlineData("ASTER://Item/abc123/", DeepLinkKind.Item, "abc123")]
    [InlineData("asterfin://item/abc123", DeepLinkKind.Item, "abc123")]
    public void ReadsLinks(string text, DeepLinkKind kind, string id) => Assert.Equal(new DeepLink(kind, id), DeepLink.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("--open")]
    [InlineData("https://item/abc123")]
    [InlineData("aster://item")]
    [InlineData("aster://item/")]
    [InlineData("aster://downloads")]
    [InlineData("aster://item/a/b")]
    [InlineData("aster://elsewhere/abc123")]
    public void IgnoresOthers(string? text) => Assert.Null(DeepLink.Parse(text));

    [Fact]
    public void FindsTheLinkAmongArguments() =>
        Assert.Equal(new DeepLink(DeepLinkKind.Play, "x"), DeepLink.Find(["--full-screen", "aster://play/x"]));

    [Fact]
    public void WritesItBack() => Assert.Equal("aster://channel/a%20b", new DeepLink(DeepLinkKind.Channel, "a b").ToString());
}
