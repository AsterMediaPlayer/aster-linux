using System.Runtime.CompilerServices;
using System.Text.Json;
using Aster.Core.DiscMenu;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>Moving about an open disc menu, on the conformance vectors' example document.</summary>
public class DiscMenuNavigatorTests
{
    private static DiscMenuDocument Example([CallerFilePath] string here = "") =>
        JsonSerializer.Deserialize<DiscMenuDocument>(
            File.ReadAllText(Path.Combine(Path.GetDirectoryName(here)!, "Conformance", "documents", "example.renderable.json")),
            JellyfinJson.Options)!;

    private static readonly DiscChapter[] Chapters = Enumerable.Range(0, 8)
        .Select(i => new DiscChapter { Index = i, Name = i == 2 ? null : $"Scene {i + 1}", StartTicks = i * 3_000_000_000L, HasImage = i != 5 })
        .ToArray();

    private static DiscMenuNavigator Open(DiscMenuDocument? document = null)
    {
        var navigator = new DiscMenuNavigator(document ?? Example() with { Chapters = Chapters }, "movie",
            (index, _) => new Uri($"https://server/chapter/{index}"));
        navigator.Open();
        return navigator;
    }

    private static void FocusOn(DiscMenuNavigator navigator, string label)
    {
        for (var tries = 0; navigator.Screen.Entries[navigator.FocusIndex].Label != label; tries++)
        {
            Assert.True(tries < 20, $"never reached {label}");
            navigator.Move(1, 0);
        }
    }

    [Fact]
    public void OpensOnTheRootsFirstButton()
    {
        var navigator = Open();
        Assert.Equal("main", navigator.Screen.Ref.Key);
        Assert.Equal(0, navigator.FocusIndex);
        Assert.False(navigator.CanGoBack);
    }

    [Fact]
    public void ASubmenuOpensAndBackReturnsToTheButtonThatOpenedIt()
    {
        var navigator = Open();
        FocusOn(navigator, "Special Features");
        navigator.Select();
        Assert.Equal("features", navigator.Screen.Ref.Key);
        Assert.Equal(0, navigator.FocusIndex);
        Assert.Equal(1, navigator.Direction);
        Assert.True(navigator.CanGoBack);

        navigator.HardwareBack();
        Assert.Equal("main", navigator.Screen.Ref.Key);
        Assert.Equal("Special Features", navigator.Screen.Entries[navigator.FocusIndex].Label);
        Assert.Equal(-1, navigator.Direction);
    }

    [Fact]
    public void BackOnTheRootCloses()
    {
        var navigator = Open();
        var closed = false;
        navigator.CloseRequested += () => closed = true;
        navigator.HardwareBack();
        Assert.True(closed);
    }

    [Fact]
    public void PlayFeaturePlaysTheTitleFromItsStart()
    {
        var navigator = Open();
        DiscPlayback? played = null;
        navigator.PlaybackRequested += p => played = p;
        navigator.Select();
        Assert.Equal(new[] { "movie" }, played!.ItemIds);
        Assert.Equal(0, played.StartTicks);
    }

    [Fact]
    public void AnExtraWithoutAnItemSaysSoRatherThanPlaying()
    {
        var navigator = Open();
        FocusOn(navigator, "Special Features");
        navigator.Select();
        string? alert = null;
        var played = false;
        navigator.Alert += a => alert = a;
        navigator.PlaybackRequested += _ => played = true;
        navigator.Select();
        Assert.False(played);
        Assert.Equal("\"Making Of\" isn't available in this library.", alert);
    }

    [Fact]
    public void SceneSelectionListsTheChaptersPagedWithBack()
    {
        var navigator = Open();
        FocusOn(navigator, "Scene Selection");
        navigator.Select();
        var screen = navigator.Screen;
        Assert.True(screen.Ref.IsChapters);
        Assert.Equal("Scene Selection", screen.Title);
        // Six to a page (the entry's PerPage), then Back and More.
        Assert.Equal(2, screen.PageCount);
        Assert.Equal(["Scene 1", "Scene 2", "Chapter 3", "Scene 4", "Scene 5", "Scene 6", "Back", "More"], screen.Entries.Select(e => e.Label));
        Assert.Equal("5:00", screen.Entries[1].Sub);
        Assert.Equal(new Uri("https://server/chapter/0"), screen.Entries[0].ThumbUrl);
        Assert.Null(screen.Entries[5].ThumbUrl);

        DiscPlayback? played = null;
        navigator.PlaybackRequested += p => played = p;
        navigator.Activate(screen.Entries[3]);
        Assert.Equal(9_000_000_000L, played!.StartTicks);

        navigator.Activate(screen.Entries[^1]);
        Assert.Equal(1, navigator.Screen.Page);
        Assert.Equal(["Scene 7", "Scene 8", "Back", "Previous"], navigator.Screen.Entries.Select(e => e.Label));
        // Back goes a page back first, then to the main menu.
        navigator.HardwareBack();
        Assert.Equal(0, navigator.Screen.Page);
        navigator.HardwareBack();
        Assert.Equal("main", navigator.Screen.Ref.Key);
    }

    [Fact]
    public void SceneSelectionWithoutChaptersSaysSo()
    {
        var navigator = Open(Example());
        string? alert = null;
        navigator.Alert += a => alert = a;
        FocusOn(navigator, "Scene Selection");
        navigator.Select();
        Assert.Equal("main", navigator.Screen.Ref.Key);
        Assert.Equal("This title has no chapter markers.", alert);
    }

    [Fact]
    public void FocusFollowsTheMeasuredFrames()
    {
        var navigator = Open();
        // A column: down goes to the next, and up from the top wraps to the bottom.
        navigator.ReportFrames([new(0, 0, 100, 20), new(0, 30, 100, 20), new(0, 60, 100, 20)], navigator.Generation);
        navigator.Move(0, 1);
        Assert.Equal(1, navigator.FocusIndex);
        navigator.Move(0, -1);
        navigator.Move(0, -1);
        Assert.Equal(2, navigator.FocusIndex);
        // Sideways, nothing's there.
        navigator.Move(1, 0);
        Assert.Equal(2, navigator.FocusIndex);
    }

    [Fact]
    public void ATapActivatesTheButtonUnderIt()
    {
        var navigator = Open();
        navigator.ReportFrames([new(0, 0, 100, 20), new(0, 30, 100, 20), new(0, 60, 100, 20)], navigator.Generation);
        navigator.Tap(50, 70);
        Assert.Equal("features", navigator.Screen.Ref.Key);
        // Frames of the screen before don't count for this one.
        navigator.Tap(50, 70);
        Assert.Equal("features", navigator.Screen.Ref.Key);
    }

    [Fact]
    public void HomeGoesBackToTheRoot()
    {
        var navigator = Open(Example() with
        {
            Menus = new Dictionary<string, DiscMenuScreenSpec>(Example().Menus)
            {
                ["deleted"] = new() { Title = "Deleted", Entries = [new() { Action = "home", Label = "Home" }] },
            },
        });
        FocusOn(navigator, "Special Features");
        navigator.Select();
        FocusOn(navigator, "Deleted Scenes");
        navigator.Select();
        navigator.Select();
        Assert.Equal("main", navigator.Screen.Ref.Key);
        Assert.False(navigator.CanGoBack);
    }
}
