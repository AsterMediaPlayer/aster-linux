// The app's song player: a queue played through mpv, with shuffle and
// repeat, lyrics, and progress reported to Jellyfin. A port of the Apple
// app's MusicPlayer.swift. (Windows' media overlay and keys are still to
// come; the Apple app's Now Playing and remote commands have no part here.)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core;
using Aster.Core.Jellyfin;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.Audio;

public enum RepeatMode
{
    Off,
    All,
    One,
}

public sealed partial class MusicPlayer : ObservableObject, IDisposable
{
    private readonly AudioEngine engine = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private JellyfinClient? client;

    /// <summary>The order songs were handed over, kept so shuffle can be undone.</summary>
    private IReadOnlyList<BaseItem> originalOrder = [];
    private string playSessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset lastReport;
    private bool hasStarted;

    public MusicPlayer()
    {
        engine.Loaded += Started;
        engine.Ended += TrackEnded;
        engine.Failed += message =>
        {
            ErrorMessage = message;
            IsPlaying = false;
        };
        clock.Tick += (_, _) => Tick();
    }

    /// <summary>Called as a queue starts, so a book playing steps aside.</summary>
    public event Action? Starting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(UpNext), nameof(HasNext))]
    public partial IReadOnlyList<BaseItem> Queue { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Current), nameof(UpNext), nameof(HasNext))]
    public partial int Index { get; private set; }

    [ObservableProperty] public partial bool IsPlaying { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentLyricIndex))]
    public partial double CurrentTime { get; private set; }

    [ObservableProperty] public partial double Duration { get; private set; }
    [ObservableProperty] public partial bool IsShuffled { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNext))]
    public partial RepeatMode RepeatMode { get; private set; }

    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    /// <summary>Lyrics for the current song, once fetched; null when the server has none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentLyricIndex))]
    public partial SongLyrics? Lyrics { get; private set; }

    public BaseItem? Current => Index >= 0 && Index < Queue.Count ? Queue[Index] : null;
    public IReadOnlyList<BaseItem> UpNext => Queue.Skip(Index + 1).Take(50).ToList();
    public bool HasNext => Index + 1 < Queue.Count || RepeatMode == RepeatMode.All;

    /// <summary>The lyric line the playhead is on, for synced lyrics.</summary>
    public int? CurrentLyricIndex
    {
        get
        {
            if (Lyrics is not { IsSynced: true } lyrics) return null;
            var ticks = (long)(CurrentTime * 10_000_000) + (lyrics.Metadata?.Offset ?? 0);
            var index = -1;
            for (var i = 0; i < lyrics.Lyrics.Count; i++)
            {
                if ((lyrics.Lyrics[i].Start ?? 0) <= ticks) index = i;
            }
            return index >= 0 ? index : null;
        }
    }

    // ---- Starting ----

    /// <summary>Plays a list of songs from one of them, optionally shuffled (the chosen song still goes first).</summary>
    public void Play(IReadOnlyList<BaseItem> tracks, int start, bool shuffled, JellyfinClient client)
    {
        var songs = tracks.Where(t => t.Kind == ItemKind.Audio).ToList();
        if (songs.Count == 0) return;
        FinishCurrent();
        this.client = client;
        originalOrder = songs;
        var at = Math.Clamp(start, 0, songs.Count - 1);
        if (shuffled)
        {
            var first = songs[at];
            Queue = [first, .. songs.Where((_, i) => i != at).OrderBy(_ => Random.Shared.Next())];
            Index = 0;
        }
        else
        {
            Queue = songs;
            Index = at;
        }
        IsShuffled = shuffled;
        Starting?.Invoke();
        Load();
    }

    /// <summary>Jumps to a song already in the queue.</summary>
    public void SkipTo(BaseItem track)
    {
        var position = Queue.ToList().FindIndex(t => t.Id == track.Id);
        if (position < 0) return;
        FinishCurrent();
        Index = position;
        Load();
    }

    // ---- Transport ----

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause(); else Resume();
    }

    public void Pause()
    {
        if (Current is null) return;
        engine.Paused = true;
        IsPlaying = false;
        Report(paused: true);
    }

    public void Resume()
    {
        if (Current is null) return;
        engine.Paused = false;
        IsPlaying = true;
        Report(paused: false);
    }

    public void Next()
    {
        if (Current is null) return;
        FinishCurrent();
        if (Index + 1 < Queue.Count) Index++;
        else if (RepeatMode == RepeatMode.All) Index = 0;
        else
        {
            StopAtEnd();
            return;
        }
        Load();
    }

    /// <summary>The start of this song, or the one before when just into it.</summary>
    public void Previous()
    {
        if (Current is null) return;
        if (CurrentTime > 3 || Index == 0)
        {
            Seek(0);
            return;
        }
        FinishCurrent();
        Index--;
        Load();
    }

    public void Seek(double seconds)
    {
        CurrentTime = Math.Clamp(seconds, 0, Math.Max(Duration, 0));
        engine.Seek(CurrentTime);
        Report(paused: !IsPlaying);
    }

    public void ToggleShuffle()
    {
        if (Current is not { } now) return;
        if (IsShuffled)
        {
            Queue = originalOrder;
            Index = Math.Max(0, originalOrder.ToList().FindIndex(t => t.Id == now.Id));
            IsShuffled = false;
        }
        else
        {
            Queue = [now, .. Queue.Where((_, i) => i != Index).OrderBy(_ => Random.Shared.Next())];
            Index = 0;
            IsShuffled = true;
        }
    }

    public void CycleRepeat() => RepeatMode = RepeatMode switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    /// <summary>Stops and clears the queue.</summary>
    public void Stop()
    {
        FinishCurrent();
        clock.Stop();
        engine.Stop();
        Queue = [];
        originalOrder = [];
        Index = 0;
        IsPlaying = false;
        CurrentTime = 0;
        Duration = 0;
        Lyrics = null;
    }

    // ---- Loading ----

    private void Load()
    {
        if (client is null || Current is not { } track)
        {
            IsPlaying = false;
            return;
        }
        playSessionId = Guid.NewGuid().ToString("N");
        hasStarted = false;
        CurrentTime = 0;
        Duration = (track.RunTimeTicks ?? 0) / 10_000_000.0;
        ErrorMessage = null;
        engine.Load(client.UniversalAudioUrl(track.Id, playSessionId, AppSettings.Current.MaxBitrate), 0, playing: true);
        IsPlaying = true;
        clock.Start();
        _ = FetchLyricsAsync(track, client);
    }

    private async Task FetchLyricsAsync(BaseItem track, JellyfinClient from)
    {
        Lyrics = null;
        SongLyrics? fetched = null;
        try { fetched = await from.LyricsAsync(track.Id); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
        if (Current?.Id == track.Id) Lyrics = fetched;
    }

    private void Started()
    {
        if (hasStarted || Current is null) return;
        hasStarted = true;
        if (engine.Duration is > 0 and var length) Duration = length;
        lastReport = DateTimeOffset.UtcNow;
        Report(paused: !IsPlaying, start: true);
    }

    private void Tick()
    {
        if (engine.Position is { } seconds) CurrentTime = seconds;
        if (hasStarted && DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromSeconds(10))
        {
            lastReport = DateTimeOffset.UtcNow;
            Report(paused: !IsPlaying);
        }
    }

    /// <summary>Reports the song that's ending so the server records it, without touching the queue.</summary>
    private void FinishCurrent()
    {
        if (!hasStarted || Current is not { } track || client is null) return;
        hasStarted = false;
        _ = client.ReportStoppedAsync(new PlaybackReport
        {
            ItemId = track.Id, PlaySessionId = playSessionId, PositionTicks = PositionTicks, IsPaused = true, PlayMethod = "DirectPlay",
        });
    }

    private void StopAtEnd()
    {
        engine.Paused = true;
        IsPlaying = false;
        CurrentTime = 0;
    }

    private void TrackEnded()
    {
        if (RepeatMode == RepeatMode.One && Current is not null)
        {
            FinishCurrent();
            Load();
            return;
        }
        Next();
    }

    private long PositionTicks => engine.Position is > 0 and var seconds ? (long)(seconds * 10_000_000) : 0;

    private void Report(bool paused, bool start = false)
    {
        if (!hasStarted || Current is not { } track || client is null) return;
        var report = new PlaybackReport
        {
            ItemId = track.Id, PlaySessionId = playSessionId, PositionTicks = PositionTicks, IsPaused = paused, PlayMethod = "DirectPlay",
        };
        // Songs start from the top each time, so no resume point is kept.
        _ = start ? client.ReportStartAsync(report) : client.ReportProgressAsync(report, ResumePoints.Saving.Never);
    }

    public void Dispose()
    {
        Stop();
        engine.Dispose();
    }
}
