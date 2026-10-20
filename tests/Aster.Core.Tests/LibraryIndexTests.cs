using Aster.Core.Browsing;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

public class LibraryIndexTests
{
    private static BaseItem Title(string name, string? sortName = null) => new() { Id = name, Name = name, SortName = sortName };

    [Theory]
    [InlineData("Écoute", "E")]
    [InlineData("apple", "A")]
    [InlineData("ｆｕｌｌ", "F")]
    [InlineData("12 Monkeys", "#")]
    [InlineData("...And Justice", "A")]
    [InlineData("Ёлка", "Е")]
    [InlineData("かぐや様", "か")]
    [InlineData("ガンダム", "か")]
    [InlineData("ぱぷりか", "は")]
    [InlineData("슬램덩크", "ㅅ")]
    [InlineData("鬼滅の刃", "#")]
    [InlineData("", "#")]
    public void SectionKeys(string text, string section) => Assert.Equal(section, LibraryIndex.SectionKey(text));

    [Fact]
    public void LeadingArticlesAreSkippedUnlessThereIsASortName()
    {
        Assert.Equal("Matrix", TitleSort.Key("The Matrix"));
        Assert.Equal("An", TitleSort.Key("An"));
        Assert.Equal("matrix, the", TitleSort.Key("The Matrix", "matrix, the"));
    }

    [Fact]
    public void SectionsAreInOrderWithLatinFirstAndNumbersLast()
    {
        var sections = LibraryIndex.Sections([
            Title("Zoo"), Title("1917"), Title("The Abyss"), Title("Ёлка"), Title("Alien"), Title("Episode 10"), Title("Episode 2"),
        ]);
        Assert.Equal(["A", "E", "Z", "Е", "#"], sections.Select(s => s.Key));
        Assert.Equal(["The Abyss", "Alien"], sections[0].Items.Select(i => i.Name));
        Assert.Equal(["Episode 2", "Episode 10"], sections[1].Items.Select(i => i.Name));
    }
}
