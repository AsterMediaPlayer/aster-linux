using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Audio;
using Avalonia.Threading;

namespace Aster.Desktop.Audio;

/// <summary>
/// Songs or a book played by Sonos rooms, grouped to play in sync: the
/// first room's speaker leads (the group's coordinator) and fetches each
/// stream from the server itself, so playing carries on without this
/// computer. Aster stays the remote: it hands over each file, plays, pauses
/// and seeks, and polls where it's got to (each second) for the progress
/// bar, chapters and the server's resume point; a file that's stopped after
/// playing has ended. Commands to a coordinator go one at a time, in order,
/// even from an output that's being replaced by another for the same rooms.
/// </summary>
public sealed class SonosOutput : IAudioOutput
{
    private static readonly Dictionary<string, Task> Queues = [];

    /// <summary>
    /// Rooms taken off their TV input for music, with the volume the TV was
    /// playing at: given back (that volume first, then the TV) when they
    /// leave, so a soundbar doesn't go back to the TV at the music's level.
    /// </summary>
    private static readonly Dictionary<string, int> TvVolumes = [];

    private readonly SonosSpeaker coordinator;
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private double position, duration;
    private DateTime polledAt = DateTime.UtcNow, loadedAt;
    private bool playing, loading, sawPlaying, stopped = true, polling;
    private int generation;

    public SonosOutput(IReadOnlyList<SonosRoom> rooms)
    {
        Rooms = rooms;
        coordinator = rooms[0].Speaker;
        poll.Tick += (_, _) => _ = PollAsync();
    }

    /// <summary>The rooms playing, the coordinator first.</summary>
    public IReadOnlyList<SonosRoom> Rooms { get; }

    /// <summary>"Kitchen", or "Kitchen + 2".</summary>
    public string Name => Rooms.Count == 1 ? Rooms[0].Name : $"{Rooms[0].Name} + {Rooms.Count - 1}";

    public event Action? Loaded;
    public event Action? Ended;
    public event Action<string>? Failed;

    public bool IsRemote => true;
    public bool CanChangeSpeed => false;

    /// <summary>The rooms grouped: the first on its own (out of any group it was in), the rest joined to it.</summary>
    public Task GroupAsync() => Enqueue(async () =>
    {
        foreach (var room in Rooms)
        {
            bool known;
            lock (TvVolumes) known = TvVolumes.ContainsKey(room.Id);
            if (known || !await Sonos.IsOnTvInputAsync(room.Speaker) || await Sonos.VolumeAsync(room.Speaker) is not { } tvVolume) continue;
            lock (TvVolumes) TvVolumes[room.Id] = tvVolume;
        }
        if (Rooms[0].Coordinator != Rooms[0].Id) await Sonos.LeaveGroupAsync(coordinator);
        foreach (var room in Rooms.Skip(1))
        {
            if (room.Coordinator != coordinator.Id) await Sonos.JoinAsync(room.Speaker, coordinator.Id);
        }
    });

    /// <summary>
    /// Done with: the rooms not in <paramref name="keep"/> leave the group,
    /// and the music stops unless the coordinator plays on for the new choice.
    /// </summary>
    public Task ReleaseAsync(IReadOnlyCollection<string> keep) => Enqueue(async () =>
    {
        if (!keep.Contains(coordinator.Id)) await Sonos.StopAsync(coordinator);
        foreach (var room in Rooms.Skip(1).Where(r => !keep.Contains(r.Id))) await Sonos.LeaveGroupAsync(room.Speaker);
        // A soundbar taken from its TV: its TV volume back first, then the TV.
        foreach (var room in Rooms.Where(r => !keep.Contains(r.Id)))
        {
            int tvVolume;
            lock (TvVolumes)
            {
                if (!TvVolumes.Remove(room.Id, out tvVolume)) continue;
            }
            await Sonos.SetVolumeAsync(room.Speaker, tvVolume);
            await Sonos.BackToTvAsync(room.Speaker);
        }
    });

    public void Load(Uri url, double start, bool playing, SonosTrack? track = null)
    {
        var at = ++generation;
        loading = true;
        sawPlaying = false;
        stopped = false;
        this.playing = playing;
        position = start;
        duration = 0;
        polledAt = loadedAt = DateTime.UtcNow;
        poll.Start();
        _ = Enqueue(async () =>
        {
            if (!await Sonos.PlayUriAsync(coordinator, url, track ?? new SonosTrack("Aster", null, null, null)))
            {
                Dispatcher.UIThread.Post(() => { if (at == generation) Fail($"{Rooms[0].Name} didn't take it."); });
                return;
            }
            await Sonos.PlayAsync(coordinator);
            if (start > 1) await Sonos.SeekAsync(coordinator, start);
            if (!playing) await Sonos.PauseAsync(coordinator);
        });
    }

    public bool Paused
    {
        set
        {
            position = Position ?? position;
            polledAt = DateTime.UtcNow;
            playing = !value;
            _ = Enqueue(() => value ? Sonos.PauseAsync(coordinator) : Sonos.PlayAsync(coordinator));
        }
    }

    /// <summary>Sonos has no playing speed: a book plays at its own pace here.</summary>
    public double Speed { set { } }

    public void Seek(double seconds)
    {
        position = Math.Max(0, seconds);
        polledAt = DateTime.UtcNow;
        _ = Enqueue(() => Sonos.SeekAsync(coordinator, position));
    }

    /// <summary>Where it's got to: the last poll, carried on by the clock while playing.</summary>
    public double? Position => stopped && !loading ? null
        : playing && sawPlaying ? position + (DateTime.UtcNow - polledAt).TotalSeconds : position;

    public double? Duration => duration > 0 ? duration : null;

    /// <summary>The group's volume is turned with <see cref="ChangeGroupVolumeAsync"/> (it's asked over the network).</summary>
    public double? ChangeVolume(double by) => null;

    /// <summary>The whole group turned by <paramref name="by"/> (of 100), as the Sonos app's group slider: its level after.</summary>
    public Task<int?> ChangeGroupVolumeAsync(int by) => Sonos.ChangeGroupVolumeAsync(coordinator, by);

    public void Stop()
    {
        generation++;
        stopped = true;
        loading = false;
        playing = false;
        poll.Stop();
        _ = Enqueue(() => Sonos.StopAsync(coordinator));
    }

    private async Task PollAsync()
    {
        if (polling || stopped) return;
        polling = true;
        try
        {
            var at = generation;
            var state = await Sonos.TransportStateAsync(coordinator);
            var where = await Sonos.PositionAsync(coordinator);
            if (at != generation || stopped) return;
            if (where is var (seconds, length))
            {
                position = seconds;
                polledAt = DateTime.UtcNow;
                if (length > 0) duration = length;
            }
            if (state == "PLAYING") sawPlaying = true;
            if (loading && (state is "PLAYING" or "PAUSED_PLAYBACK") && sawPlaying || loading && state == "PAUSED_PLAYBACK" && !playing)
            {
                loading = false;
                Loaded?.Invoke();
            }
            else if (loading && DateTime.UtcNow - loadedAt > TimeSpan.FromSeconds(20))
            {
                Fail($"{Rooms[0].Name} couldn't play this.");
            }
            else if (!loading && sawPlaying && state == "STOPPED")
            {
                // Played to its end: Sonos stops on the last of what it was handed.
                stopped = true;
                playing = false;
                poll.Stop();
                Ended?.Invoke();
            }
        }
        finally { polling = false; }
    }

    private void Fail(string why)
    {
        loading = false;
        stopped = true;
        playing = false;
        poll.Stop();
        Failed?.Invoke(why);
    }

    /// <summary>After whatever was asked of this coordinator before, by this output or another.</summary>
    private Task Enqueue(Func<Task> command)
    {
        lock (Queues)
        {
            var before = Queues.GetValueOrDefault(coordinator.Id) ?? Task.CompletedTask;
            var next = before.ContinueWith(_ => command(), TaskScheduler.Default).Unwrap();
            Queues[coordinator.Id] = next.ContinueWith(_ => { }, TaskScheduler.Default);
            return next;
        }
    }

    public void Dispose() => poll.Stop();
}
