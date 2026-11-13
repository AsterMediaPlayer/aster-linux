using Aster.Core.Diagnostics;

namespace Aster.Core.Tests;

/// <summary>A debug report's lines with what signs in, where the server is and who's using it taken out. All values here are made up.</summary>
public sealed class RedactTests
{
    private static string Clean(string s, params string[] names) => Redact.Text(s, names);

    [Fact]
    public void TokensInAddressesAndHeaders()
    {
        Assert.Equal("GET https://<server>/Videos/1/stream?api_key=<hidden>&Static=true",
            Clean("GET https://media.example.org:8920/Videos/1/stream?api_key=0123456789abcdef0123456789abcdef&Static=true"));
        Assert.Equal("X-Emby-Token: <hidden>", Clean("X-Emby-Token: abcdef0123456789"));
        Assert.Equal("…?X-Plex-Token=<hidden>&x=1", Clean("…?X-Plex-Token=AbC-dEf_123&x=1"));
        Assert.Equal("""{"AccessToken":"<hidden>","User":1}""", Clean("""{"AccessToken":"feedfacefeedface","User":1}"""));
        Assert.Equal("""MediaBrowser Client="Aster", DeviceId="<hidden>", Token="<hidden>" """.TrimEnd(),
            Clean("""MediaBrowser Client="Aster", DeviceId="abc123", Token="secret-value" """.TrimEnd()));
        Assert.Equal("Authorization: Bearer <hidden>", Clean("Authorization: Bearer abcdefghijklmnop"));
        Assert.Equal("""{"Username":"x","Pw":"<hidden>"}""", Clean("""{"Username":"x","Pw":"hunter22"}"""));
    }

    [Fact]
    public void AddressesAndHosts()
    {
        Assert.Equal("http://<server>/emby/System/Info", Clean("http://192.0.2.10:8096/emby/System/Info"));
        Assert.Equal("connecting to <server>", Clean("connecting to 198-51-100-7.0123456789abcdef0123456789abcdef.plex.direct"));
        Assert.Equal("from <ip> and <ip>", Clean("from 203.0.113.5 and 2001:db8:0:0:0:0:0:1"));
        Assert.Equal("mail <email>", Clean("mail someone@example.com"));
    }

    [Fact]
    public void LibraryPathsKeepOnlyTheirKind()
    {
        Assert.Equal("\"Path\":\"<path>.mkv\"", Clean("\"Path\":\"/mnt/Anime/Some Show (2023)/Season 1/S01E01.mkv\""));
        Assert.Equal("opened <path>.m4b", Clean(@"opened D:\Books\An Author\A Book.m4b"));
    }

    [Fact]
    public void TheHomeFolderNamesNoOne()
    {
        Assert.Equal("settings in ~/.local/share/Aster", Redact.Text("settings in /home/someone/.local/share/Aster", home: "/home/someone"));
    }

    [Fact]
    public void TheUsersNameGoesWhereverItIs()
    {
        Assert.Equal("Signed in as <name> on <name>'s server", Clean("Signed in as Jordan on jordan's server", "Jordan"));
        // Only as a whole word: a longer word that holds it stays.
        Assert.Equal("Jordanian food", Clean("Jordanian food", "Jordan"));
    }

    /// <summary>A server called "Server" is no clue to whose it is, and a placeholder put in already isn't touched.</summary>
    [Fact]
    public void GenericNamesAndPlaceholdersStay()
    {
        Assert.Equal("GET http://<server>/Items: the server's settings", Clean("GET http://192.0.2.1:8096/Items: the server's settings", "Server"));
    }

    [Fact]
    public void TheAccountsIdGoesWithAndWithoutDashes()
    {
        const string id = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
        Assert.Equal("/Users/<name>/Views?userId=<name>", Clean($"/Users/{id}/Views?userId={id.Replace("-", "")}", id));
    }

    [Fact]
    public void WhatsUsefulStays()
    {
        const string line = "Playback failed: loading failed, video hevc 3840x2160 10-bit, audio truehd 8ch, direct play, item 4a1f";
        Assert.Equal(line, Clean(line));
        Assert.Contains("00:12:31.4", Clean("mpv [ffmpeg] error at 00:12:31.4"));
        Assert.DoesNotContain("<ip>", Clean("version 0.10.4 on Linux 6.18.55.2"));
    }
}

public sealed class StartTimingTests
{
    [Fact]
    public void EachStepSinceTheOneBefore()
    {
        long clock = 1_000;
        var t = new StartTiming(() => clock);
        clock += 120; t.Mark("item");
        clock += 300; t.Mark("server's playback answer");
        t.Method = "transcode";
        clock += 5_000; t.Mark("stream opened", once: true);
        clock += 50; t.Mark("stream opened", once: true);
        clock += 40; t.Mark("first picture");
        Assert.Equal("Start (transcode) took 5.51 s: item 0.12 s, server's playback answer +0.30 s, stream opened +5.00 s, first picture +0.09 s", t.Summary());
    }

    [Fact]
    public void LeavingEarlySaysHowFarItGot()
    {
        long clock = 0;
        var t = new StartTiming(() => clock);
        clock += 200; t.Mark("item");
        clock += 8_000;
        Assert.Equal("Start left after 8.20 s before the first picture; it had reached: item 0.20 s", t.Summary(leftEarly: true));
    }
}

public sealed class DebugReportTests
{
    [Fact]
    public void SettingsLeaveOutWhatNamesAPlaceOrAPerson()
    {
        var settings = new AppSettings { SonosSpeaker = "RINCON_000E58123456", WelcomeSeen = ["abc"], AutoSkipIntro = true };
        var fields = DebugReport.Settings(settings);
        Assert.Equal("true", fields["AutoSkipIntro"]);
        Assert.Equal("120000000", fields["MaxBitrate"]);
        Assert.DoesNotContain(fields.Keys, k => k.Contains("Sonos", StringComparison.OrdinalIgnoreCase) || k.Contains("Welcome", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheReportIsInTheServicesShape()
    {
        DebugLog.Note("Playback", "Started from http://192.0.2.10:8096/Videos/1/stream?api_key=secret for Jordan");
        var report = DebugReport.Build(new DebugReportContext { AppVersion = "0.10.4", ServerKind = "jellyfin", ServerVersion = "10.11.0", Names = ["Jordan"] },
            "  ", crash: null, contactEmail: " me@example.com ", settings: new AppSettings());
        Assert.Equal("Sent from Settings > About.", (string?)report["description"]);
        Assert.Equal("0.10.4", (string?)report["app"]!["version"]);
        Assert.Equal("jellyfin", (string?)report["server"]!["type"]);
        Assert.Equal("me@example.com", (string?)report["contactEmail"]);
        var logs = (string)report["logs"]!;
        Assert.Contains("== This run", logs);
        Assert.Contains("http://<server>/Videos/1/stream?api_key=<hidden> for <name>", logs);
        Assert.DoesNotContain("secret", logs);
        Assert.DoesNotContain("192.0.2.10", logs);
    }
}
