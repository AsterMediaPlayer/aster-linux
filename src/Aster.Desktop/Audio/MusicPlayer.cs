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
    /// <summary>This computer's own player, used whenever no speakers are chosen.</summary>
    private readonly AudioEngine local = new();

    /// <summary>Where the songs play: <see cref="local"/>, or Sonos rooms.</summary>
    private IAudioOutput engine;

    /// <summary>The player's own volume turned by <paramref name="by"/>: the level after.</summary>
    public double? ChangeVolume(double by) => engine.ChangeVolume(by);

    /// <summary>
    /// Plays through <paramref name="output"/> from now on (null: this
    /// computer), carrying on with the song where it was when
    /// <paramref name="carryOn"/>.
    /// </summary>
    public void UseOutput(IAudioOutput? output, bool carryOn = true)
    {
        var next = output ?? local;
        if (ReferenceEquals(next, engine)) return;
        var at = engine.Position ?? CurrentTime;
        var wasPlaying = IsPlaying;
        FinishCurrent();
        engine.Stop();
        Hook(engine, on: false);
        engine = next;
        Hook(engine, on: true);
        if (carryOn && Current is not null && client is not null) Load(at, wasPlaying);
    }

    private void Hook(IAudioOutput output, bool on)
    {
        if (on)
        {
            output.Loaded += Started;
            output.Ended += TrackEnded;
            output.Failed += Fail;
            if (output is AudioEngine gapless) gapless.Advanced += RanOn;
        }
        else
        {
            output.Loaded -= Started;
            output.Ended -= TrackEnded;
            output.Failed -= Fail;
            if (output is AudioEngine gapless) gapless.Advanced -= RanOn;
        }
    }

    private void Fail(string message)
    {
        ErrorMessage = message;
        IsPlaying = false;
    }
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private JellyfinClient? client;

    /// <summary>The order songs were handed over, kept so shuffle can be undone.</summary>
    private IReadOnlyList<BaseItem> originalOrder = [];
    private string playSessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset lastReport;
    private bool hasStarted;

    /// <summary>Save upcoming songs: the next few, saved as the one playing gets under way.</summary>
    private readonly Aster.Core.Audio.MusicAhead ahead = new();

    /// <summary>The song (by its place in the queue) the songs after it were last asked to be saved for; -1, none yet.</summary>
    private int aheadFor = -1;

    public MusicPlayer()
    {
        engine = local;
        Hook(engine, on: true);
        clock.Tick += (_, _) => Tick();
    }

    /// <summary>Called as a queue starts, so a book playing steps aside.</summary>
    public event Action? Starting;

    /// <summary>A queue was played, from any page, for the player to open.</summary>
    public event Action? Played;

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
        Played?.Invoke();
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
        ahead.Clear();
        aheadFor = -1;
        Index = 0;
        IsPlaying = false;
        CurrentTime = 0;
        Duration = 0;
        Lyrics = null;
    }

    // ---- Loading ----

    private void Load(double start = 0, bool playing = true)
    {
        if (client is null || Current is not { } track)
        {
            IsPlaying = false;
            return;
        }
        playSessionId = Guid.NewGuid().ToString("N");
        hasStarted = false;
        CurrentTime = start;
        Duration = (track.RunTimeTicks ?? 0) / 10_000_000.0;
        ErrorMessage = null;
        var about = new Aster.Core.Audio.SonosTrack(track.Name ?? "", track.AlbumArtist ?? track.Artists?.FirstOrDefault(), track.Album,
            client.ImageUrl(track.AlbumId ?? track.Id, ImageKind.Primary, track.AlbumId is null ? null : track.AlbumPrimaryImageTag, maxWidth: 600));
        if (engine.IsRemote) _ = LoadOnSpeakersAsync(engine, client, track, start, playing, about);
        else engine.Load(SongUrl(client, track, playSessionId), start, playing, about);
        IsPlaying = playing;
        QueueFollowing();
        clock.Start();
        _ = FetchLyricsAsync(track, client);
    }

    /// <summary>The song handed to the speakers as its own file, its format looked up first when the list didn't say.</summary>
    private async Task LoadOnSpeakersAsync(IAudioOutput speakers, JellyfinClient from, BaseItem track, double start, bool playing, Aster.Core.Audio.SonosTrack about)
    {
        var container = track.Container ?? track.MediaSources?.FirstOrDefault()?.Container;
        if (container is null)
        {
            try { container = (await from.ItemAsync(track.Id)) is { } detail ? detail.Container ?? detail.MediaSources?.FirstOrDefault()?.Container : null; }
            catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
        }
        // Moved on meanwhile: another song, or back to this computer.
        if (Current?.Id != track.Id || !ReferenceEquals(engine, speakers)) return;
        speakers.Load(from.SonosAudioUrl(track.Id, container), start, playing, about);
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
        SaveAhead();
        if (hasStarted && DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromSeconds(10))
        {
            lastReport = DateTimeOffset.UtcNow;
            Report(paused: !IsPlaying);
        }
    }

    /// <summary>Reports the song that's ending so the server records it, without touching the queue.</summary>
    /// <param name="atEnd">Played to its end, the next already playing: its length, rather than where the player is.</param>
    private void FinishCurrent(bool atEnd = false)
    {
        if (!hasStarted || Current is not { } track || client is null) return;
        hasStarted = false;
        _ = client.ReportStoppedAsync(new PlaybackReport
        {
            ItemId = track.Id, PlaySessionId = playSessionId, PlayMethod = "DirectPlay", IsPaused = true,
            PositionTicks = atEnd ? track.RunTimeTicks ?? PositionTicks : PositionTicks,
        });
    }

    private void StopAtEnd()
    {
        engine.Paused = true;
        IsPlaying = false;
        CurrentTime = 0;
    }

    /// <summary>Settings ▸ Music's streaming quality: the server converts songs over it; Original (0), none.</summary>
    public static int MusicBitrate => AppSettings.Current.MusicMaxBitrate is > 0 and var cap ? cap : 999_000_000;

    /// <summary>
    /// Gapless playback (Settings ▸ Music): the song after this one queued
    /// in mpv, to run on from it with no pause between; on this computer
    /// only, and not with a song repeating.
    /// </summary>
    private void QueueFollowing()
    {
        if (engine is not AudioEngine local || client is null) return;
        var following = !AppSettings.Current.GaplessMusic || RepeatMode == RepeatMode.One || Current is null ? -1
            : Index + 1 < Queue.Count ? Index + 1
            : RepeatMode == RepeatMode.All ? 0 : -1;
        local.QueueNext(following >= 0 ? SongUrl(client, Queue[following], Guid.NewGuid().ToString("N")) : null);
    }

    /// <summary>The song's saved file, when Save upcoming songs has it; else its stream.</summary>
    private Uri SongUrl(JellyfinClient from, BaseItem track, string session) =>
        ahead.SavedFile(track.Id) is { } file ? new Uri(file) : from.UniversalAudioUrl(track.Id, session, MusicBitrate);

    /// <summary>
    /// Save upcoming songs (Settings ▸ Music, on unless turned off): once the
    /// song playing is ten seconds in, the next five (round again with Repeat
    /// All) are saved, on this computer only. Off, anything saved goes.
    /// </summary>
    private void SaveAhead()
    {
        if (!AppSettings.Current.SaveUpcomingSongs)
        {
            if (aheadFor != -2) ahead.Clear();
            aheadFor = -2;
            return;
        }
        if (engine is not AudioEngine || client is not { } from || !hasStarted || CurrentTime < 10 || aheadFor == Index || Current is not { } playing) return;
        aheadFor = Index;
        var upcoming = Enumerable.Range(1, Aster.Core.Audio.MusicAhead.Ahead)
            .Select(step => Index + step < Queue.Count ? Index + step : RepeatMode == RepeatMode.All ? (Index + step) % Queue.Count : -1)
            .TakeWhile(i => i >= 0 && i != Index)
            .Select(i => (Queue[i].Id, from.SavedAudioUrl(Queue[i].Id, MusicBitrate)))
            .ToList();
        ahead.Want(upcoming, playing.Id);
    }

    partial void OnQueueChanged(IReadOnlyList<BaseItem> value)
    {
        QueueFollowing();
        // Another order: what's coming up is asked again.
        if (aheadFor >= 0) aheadFor = -1;
    }
    partial void OnRepeatModeChanged(RepeatMode value) => QueueFollowing();

    /// <summary>The queued song ran on from the last, with no gap: the queue moves on to it, without loading it again.</summary>
    private void RanOn()
    {
        if (Current is null || client is null) return;
        FinishCurrent(atEnd: true);
        Index = Index + 1 < Queue.Count ? Index + 1 : 0;
        if (Current is not { } track) return;
        playSessionId = Guid.NewGuid().ToString("N");
        hasStarted = false;
        CurrentTime = 0;
        Duration = (track.RunTimeTicks ?? 0) / 10_000_000.0;
        ErrorMessage = null;
        // Opened ahead and playing already: started now (mpv's length may still be the last song's, so the list's is kept).
        hasStarted = true;
        lastReport = DateTimeOffset.UtcNow;
        Report(paused: !IsPlaying, start: true);
        QueueFollowing();
        _ = FetchLyricsAsync(track, client);
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
        local.Dispose();
        ahead.Dispose();
    }
}
