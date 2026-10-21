using Aster.Core.Audio;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

public class AudiobookTests
{
    private static BaseItem Folder(string id, string name, string? parent) => new() { Id = id, Name = name, Type = "Folder", ParentId = parent };

    private static BaseItem File(string id, string name, string parent, string? album = null, int? index = null, long minutes = 10, UserItemData? data = null, IReadOnlyList<ChapterInfo>? chapters = null) =>
        new()
        {
            Id = id, Name = name, Type = "AudioBook", ParentId = parent, Album = album, IndexNumber = index,
            RunTimeTicks = minutes * 60 * 10_000_000, UserData = data, Chapters = chapters,
        };

    // library/
    //   Brandon Sanderson/
    //     Mistborn/            (a series of one-file books)
    //       1 The Final Empire.m4b
    //       2 The Well of Ascension.m4b
    //     Warbreaker/          (a book in parts)
    //       part 1.mp3, part 2.mp3
    private static readonly IReadOnlyList<BaseItem> Folders =
    [
        Folder("lib", "Audiobooks", null),
        Folder("bs", "Brandon Sanderson", "lib"),
        Folder("mist", "Mistborn", "bs"),
        Folder("war", "Warbreaker", "bs"),
    ];

    private static readonly IReadOnlyList<BaseItem> Files =
    [
        File("fe", "The Final Empire", "mist", index: 1),
        File("woa", "The Well of Ascension", "mist", index: 2),
        File("w2", "Part 2", "war", album: "Warbreaker", index: 2),
        File("w1", "Part 1", "war", album: "Warbreaker", index: 1),
    ];

    [Fact]
    public void FilesGatherIntoBooksWithAuthorsAndSeriesFromFolders()
    {
        var books = AudiobookLibrary.Books(Files, Folders).OrderBy(b => b.Title).ToList();
        Assert.Equal(["The Final Empire", "The Well of Ascension", "Warbreaker"], books.Select(b => b.Title));
        Assert.All(books, b => Assert.Equal("Brandon Sanderson", b.Author));

        var empire = books[0];
        Assert.Equal("Mistborn", empire.Series);
        Assert.Equal(1, empire.SeriesIndex);

        // Its folder holds just this book, so it's the book's own, not a series.
        var warbreaker = books[2];
        Assert.Null(warbreaker.Series);
        Assert.Equal(["w1", "w2"], warbreaker.Parts.Select(p => p.Id));
        Assert.Equal("w1", warbreaker.Id);
    }

    [Fact]
    public void ChaptersComeFromTheFileOrFromItsParts()
    {
        var parts = AudiobookLibrary.Books(Files, Folders).Single(b => b.Title == "Warbreaker");
        Assert.Equal(["Part 1", "Part 2"], parts.Chapters.Select(c => c.Title));
        Assert.Equal(1, parts.Chapters[1].Part);
        Assert.Equal(600, parts.OffsetOfPart(1));

        var m4b = Audiobook.FromItem(File("x", "One", "lib", chapters:
        [
            new ChapterInfo { StartPositionTicks = 600_000_000, Name = "Two" },
            new ChapterInfo { StartPositionTicks = 0, Name = "" },
        ]));
        Assert.Equal(["Chapter 1", "Two"], m4b.Chapters.Select(c => c.Title));
        Assert.Equal(60, m4b.Chapters[1].Start);
        Assert.Empty(Audiobook.FromItem(File("y", "Plain", "lib")).Chapters);
    }

    [Fact]
    public void ResumePointFollowsTheLastPartListenedTo()
    {
        var book = AudiobookLibrary.Books(Files, Folders).Single(b => b.Title == "Warbreaker");
        Assert.Null(book.ResumePoint);
        Assert.Equal(0, book.Progress);

        var halfway = book.Updating([book.Parts[0] with { UserData = new UserItemData { PlaybackPositionTicks = 300 * 10_000_000L } }]);
        Assert.Equal((0, 300.0), halfway.ResumePoint);
        Assert.Equal(0.25, halfway.Progress, 3);

        var firstDone = book.Updating([book.Parts[0] with { UserData = new UserItemData { Played = true } }]);
        Assert.Equal((1, 0.0), firstDone.ResumePoint);

        var finished = book.Updating(book.Parts.Select(p => p with { UserData = new UserItemData { Played = true } }));
        Assert.Null(finished.ResumePoint);
        Assert.True(finished.IsFinished);
        Assert.Equal(1, finished.Progress);
    }

    [Fact]
    public void ShelvesGroupByAuthorAndOrderASeries()
    {
        var books = AudiobookLibrary.Books(Files, Folders);
        var series = Assert.Single(AudiobookLibrary.Shelves(books, AudiobookShelfKind.Series));
        Assert.Equal("Mistborn", series.Name);
        Assert.Equal(["The Final Empire", "The Well of Ascension"], series.Books.Select(b => b.Title));
        var author = Assert.Single(AudiobookLibrary.Shelves(books, AudiobookShelfKind.Author));
        Assert.Equal(3, author.Books.Count);
    }

    [Fact]
    public void RateLabels()
    {
        Assert.Equal("1×", AudiobookRates.Label(1));
        Assert.Equal("1.25×", AudiobookRates.Label(1.25));
    }
}
