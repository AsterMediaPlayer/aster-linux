using System.Net;
using System.Text.Json;

namespace Aster.Core.Tests;

/// <summary>Each test has its own settings file and user, since the sync follows one user at a time.</summary>
public sealed class SettingsSyncTests : IDisposable
{
    private readonly string file = Path.Combine(Path.GetTempPath(), $"aster-settings-{Guid.NewGuid():N}.json");

    public SettingsSyncTests() => AppSettings.UseFile(file);

    public void Dispose() => File.Delete(file);

    /// <summary>A server whose display preferences hold <paramref name="prefs"/>, taking any save.</summary>
    private static StubServer Server(string prefs) => new(request => request.Method == HttpMethod.Get
        ? (HttpStatusCode.OK, """{"Id":"x","CustomPrefs":""" + prefs + "}")
        : (HttpStatusCode.NoContent, ""));

    private static JsonElement SavedPrefs(StubServer server) =>
        JsonDocument.Parse(server.Requests.Single(r => r.Method == HttpMethod.Post).Body!).RootElement.GetProperty("CustomPrefs");

    [Fact]
    public async Task PullTakesTheServersValuesAndSendsNothingBack()
    {
        var server = Server("""{"autoplayNext":"false","styledSubtitles":"false"}""");
        await SettingsSync.PullAsync(server.Client(userId: "pull"));

        Assert.False(AppSettings.Current.AutoplayNext);
        Assert.False(AppSettings.Current.StyledSubtitles);
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task PullSendsUpOnlyWhatWasChosenHere()
    {
        AppSettings.Save(new AppSettings { AutoSkipIntro = true, Chosen = ["autoSkipIntro"] });
        var server = Server("""{"theme":"dark"}""");
        await SettingsSync.PullAsync(server.Client(userId: "send"));

        var prefs = SavedPrefs(server);
        Assert.Equal("true", prefs.GetProperty("autoSkipIntro").GetString());
        Assert.Equal("dark", prefs.GetProperty("theme").GetString());
        // Never chosen here: left for another device to set.
        Assert.False(prefs.TryGetProperty("autoplayNext", out _));
    }

    [Fact]
    public async Task ChangeIsSavedOnlyOnceTheServersValuesAreKnown()
    {
        var server = Server("""{"autoplayNext":"true"}""");
        var client = server.Client(userId: "change");
        await SettingsSync.ChangeAsync(client, s => s with { AutoplayNext = false });
        Assert.Empty(server.Requests);
        Assert.Contains("autoplayNext", AppSettings.Current.Chosen);

        // The server's "true" is newer than nothing; it's what the pull finds that counts.
        await SettingsSync.PullAsync(client);
        Assert.True(AppSettings.Current.AutoplayNext);
        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Post);

        await SettingsSync.ChangeAsync(client, s => s with { AutoplayNext = false });
        Assert.Equal("false", SavedPrefs(server).GetProperty("autoplayNext").GetString());
    }

    [Fact]
    public async Task ThemeMusicAndVideosFollowTheUserUnderTheAppleAppsKeys()
    {
        Assert.False(AppSettings.Current.ThemeMusic);
        Assert.False(AppSettings.Current.ThemeVideos);
        var server = Server("""{"themeMusic":"true"}""");
        var client = server.Client(userId: "themes");
        await SettingsSync.PullAsync(client);
        Assert.True(AppSettings.Current.ThemeMusic);

        await SettingsSync.ChangeAsync(client, s => s with { ThemeVideos = true });
        Assert.Equal("true", SavedPrefs(server).GetProperty("themeVideos").GetString());
    }

    [Fact]
    public async Task ComputerOnlySettingsStayOffTheServer()
    {
        var server = Server("{}");
        var client = server.Client(userId: "local");
        await SettingsSync.PullAsync(client);
        await SettingsSync.ChangeAsync(client, s => s with { MaxBitrate = 20_000_000, FullScreenPlayback = false });

        Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Post);
        Assert.Equal(20_000_000, AppSettings.Current.MaxBitrate);
        Assert.False(AppSettings.Current.FullScreenPlayback);
    }
}
