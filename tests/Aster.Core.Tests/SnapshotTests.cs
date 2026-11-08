using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>A screen's last answers, kept on disk to show at once next time.</summary>
public sealed class SnapshotTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aster-snapshots-" + Guid.NewGuid().ToString("N"));

    public SnapshotTests() => Snapshots.UseFolder(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private sealed record Kept(IReadOnlyList<BaseItem> Items);

    [Fact]
    public async Task AServersItemsComeBackAsTheyWere()
    {
        var item = JsonSerializer.Deserialize<BaseItem>("""
            {"Id":"a1","Name":"Arrival","Type":"Movie","ProductionYear":2016,"Width":3840,"Height":2160,
             "ImageTags":{"Primary":"p1"},"BackdropImageTags":["b1"],
             "UserData":{"PlaybackPositionTicks":600000000,"PlayedPercentage":12.5,"Played":false}}
            """, JellyfinJson.Options)!;
        var key = Snapshots.Key(new Uri("https://media.example/"), "u1", "home");
        await Snapshots.WriteAsync(key, new Kept([item]));

        var back = (await Snapshots.ReadAsync<Kept>(key))!.Items.Single();
        Assert.Equal(JsonSerializer.Serialize(item, JellyfinJson.Options), JsonSerializer.Serialize(back, JellyfinJson.Options));
        Assert.True(back.Is4K);
    }

    [Fact]
    public async Task OnePersonsScreenNeverShowsForAnother()
    {
        var server = new Uri("https://media.example");
        await Snapshots.WriteAsync(Snapshots.Key(server, "u1", "home"), new Kept([]));
        Assert.Null(await Snapshots.ReadAsync<Kept>(Snapshots.Key(server, "u2", "home")));
        Assert.Null(await Snapshots.ReadAsync<Kept>(Snapshots.Key(new Uri("https://other.example"), "u1", "home")));
        Assert.NotNull(await Snapshots.ReadAsync<Kept>(Snapshots.Key(new Uri("https://media.example/"), "u1", "home")));
    }

    [Fact]
    public async Task SigningOutForgetsThemAndABrokenFileIsNothing()
    {
        var key = Snapshots.Key(new Uri("https://media.example"), "u1", "home");
        await Snapshots.WriteAsync(key, new Kept([]));
        Snapshots.Clear();
        Assert.Null(await Snapshots.ReadAsync<Kept>(key));

        await Snapshots.WriteAsync(key, new Kept([]));
        var file = Directory.GetFiles(folder).Single();
        await File.WriteAllTextAsync(file, "{\"Items\":[{\"Id\"");
        Assert.Null(await Snapshots.ReadAsync<Kept>(key));
    }
}
