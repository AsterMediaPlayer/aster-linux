using System.Diagnostics;
using Aster.Mpv;

namespace Aster.Core.Tests;

/// <summary>
/// Drives the real libmpv with a small file made by ffmpeg: two audio
/// tracks and a subtitle track, as a Jellyfin file would have. Skipped
/// without ffmpeg on the PATH.
/// </summary>
public sealed class MpvPlayerTests : IDisposable
{
    private readonly string folder = Directory.CreateTempSubdirectory("aster-mpv-").FullName;

    public void Dispose() => Directory.Delete(folder, recursive: true);

    private string? MakeClip()
    {
        var srt = Path.Combine(folder, "subs.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:01,500\nHello\n");
        var clip = Path.Combine(folder, "clip.mkv");
        var ffmpeg = new ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-v", "error", "-f", "lavfi", "-i", "testsrc=duration=3:size=320x180:rate=25",
                "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
                "-f", "lavfi", "-i", "sine=frequency=880:duration=3",
                "-i", srt,
                "-map", "0", "-map", "1", "-map", "2", "-map", "3",
                "-metadata:s:a:0", "language=eng", "-metadata:s:a:1", "language=jpn",
                "-c:v", "libx264", "-c:a", "aac", "-c:s", "srt", clip,
            },
            RedirectStandardError = true,
        };
        try
        {
            using var process = Process.Start(ffmpeg)!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? clip : throw new InvalidOperationException("ffmpeg failed: " + errors);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static MpvPlayer Headless() => MpvPlayer.Create(new Dictionary<string, string>
    {
        ["vo"] = "null",
        ["ao"] = "null",
        ["idle"] = "yes",
        ["keep-open"] = "yes",
        ["pause"] = "yes",
    });

    [Fact]
    public async Task LoadsAFileAndListsItsTracksByContainerIndex()
    {
        if (MakeClip() is not { } clip) return;
        using var player = Headless();
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        player.FileLoaded += () => loaded.TrySetResult();

        await player.CommandAsync("loadfile", clip);
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var tracks = player.Tracks();
        Assert.Equal(["video", "audio", "audio", "sub"], tracks.Select(t => t.Type));
        Assert.Equal([0, 1, 2, 3], tracks.Select(t => t.FfIndex ?? -1));
        Assert.Equal("jpn", tracks.Single(t => t is { Type: "audio", Id: 2 }).Language);
        Assert.Equal(3, player.GetDouble("duration")!.Value, 0);

        player.SetProperty("aid", "2");
        Assert.Equal("2", player.GetString("aid"));
    }

    [Fact]
    public async Task ReportsWatchedPropertiesAndAFailedLoad()
    {
        using var player = Headless();
        var paused = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<MpvEndFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.PropertyChanged += (name, value) => { if (name == "pause") paused.TrySetResult(value); };
        player.EndFile += end => ended.TrySetResult(end);

        player.Observe("pause", typeof(bool));
        Assert.Equal(true, await paused.Task.WaitAsync(TimeSpan.FromSeconds(5)));

        await player.CommandAsync("loadfile", Path.Combine(folder, "missing.mkv"));
        var end = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(MpvEndFileReason.Error, end.Reason);
        Assert.NotNull(end.ErrorMessage);
    }

    [Fact]
    public async Task AFailedCommandThrows()
    {
        using var player = Headless();
        await Assert.ThrowsAsync<MpvException>(() => player.CommandAsync("no-such-command"));
        Assert.Throws<MpvException>(() => player.SetProperty("no-such-property", "1"));
    }
}
