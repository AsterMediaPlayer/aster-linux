// Plays one audiobook at a time through mpv: picks up where Jellyfin says
// listening left off, moves between chapters and parts, plays at the speed
// chosen for the book, and skips back 15 seconds or ahead 30. Progress goes
// back to Jellyfin so another device carries on from the same place. A
// port of the Apple app's AudiobookPlayer.swift; its Listen Ahead cache and
// downloads (iPhone only) have no part here.

using System;
using System.Linq;
using Aster.Core;
using Aster.Core.Audio;
using Aster.Core.Jellyfin;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.Audio;

public sealed partial class AudiobookPlayer : ObservableObject, IDisposable
{
    public const double SkipBackSeconds = 15;
    public const double SkipForwardSeconds = 30;

    private readonly AudioEngine engine = new();
    private readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private JellyfinClient? client;
    private string playSessionId = Guid.NewGuid().ToString("N");
    private DateTimeOffset lastReport;
    private bool hasStarted;

    /// <summary>Whether to play once the part is open (else it waits, paused).</summary>
    private bool playWhenReady = true;

    public AudiobookPlayer()
    {
        engine.Loaded += Started;
        engine.Ended += PartEnded;
        engine.Failed += message =>
        {
            ErrorMessage = message;
            IsPlaying = false;
        };
        clock.Tick += (_, _) => Tick();
    }

    /// <summary>Called as a book starts, so songs playing step aside.</summary>
    public event Action? Starting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChapter), nameof(BookElapsed), nameof(BookDuration), nameof(Chapters))]
    public partial Audiobook? Book { get; private set; }

    /// <summary>The part (file) playing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChapter), nameof(BookElapsed))]
    public partial int PartIndex { get; private set; }

    [ObservableProperty] public partial bool IsPlaying { get; private set; }

    /// <summary>Seconds into the part playing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentChapter), nameof(BookElapsed))]
    public partial double CurrentTime { get; private set; }

    /// <summary>The part's length.</summary>
    [ObservableProperty] public partial double Duration { get; private set; }

    [ObservableProperty] public partial double Rate { get; private set; } = 1;
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }

    public System.Collections.Generic.IReadOnlyList<AudiobookChapter> Chapters => Book?.Chapters ?? [];

    /// <summary>The chapter the playhead is in.</summary>
    public AudiobookChapter? CurrentChapter =>
        Chapters.LastOrDefault(c => c.Part < PartIndex || (c.Part == PartIndex && c.Start <= CurrentTime + 0.5));

    /// <summary>How far into the whole book, and its whole length.</summary>
    public double BookElapsed => (Book?.OffsetOfPart(PartIndex) ?? 0) + CurrentTime;
    public double BookDuration => Book?.Duration ?? 0;

    /// <summary>Where the current chapter starts and ends in the part playing, for its progress bar; the whole part when there are no chapters.</summary>
    public (double Start, double End) ChapterBounds
    {
        get
        {
            if (CurrentChapter is not { } chapter || chapter.Part != PartIndex) return (0, Duration);
            var next = Chapters.FirstOrDefault(c => c.Id == chapter.Id + 1);
            var end = next is not null && next.Part == PartIndex ? next.Start : Duration;
            return (chapter.Start, Math.Max(end, chapter.Start));
        }
    }

    // ---- Starting ----

    /// <summary>
    /// Plays <paramref name="book"/>: from <paramref name="chapter"/> when
    /// given, else from where listening left off. The book already playing
    /// just carries on.
    /// </summary>
    public void Play(Audiobook book, AudiobookChapter? chapter, JellyfinClient client)
    {
        if (Book?.Id == book.Id)
        {
            if (chapter is not null) JumpTo(chapter);
            if (!IsPlaying) Resume();
            return;
        }
        Starting?.Invoke();
        FinishCurrent();
        this.client = client;
        Book = book;
        Rate = AudiobookRates.Rate(book.Id);
        engine.Speed = Rate;
        var start = chapter is not null ? (chapter.Part, chapter.Start) : book.ResumePoint ?? (0, 0);
        Load(start.Item1, start.Item2, playing: true);
    }

    // ---- Transport ----

    public void TogglePlayPause()
    {
        if (IsPlaying) Pause(); else Resume();
    }

    public void Pause()
    {
        if (Book is null) return;
        engine.Paused = true;
        playWhenReady = false;
        IsPlaying = false;
        CurrentTime = engine.Position ?? CurrentTime;
        Report(paused: true);
    }

    public void Resume()
    {
        if (Book is null) return;
        playWhenReady = true;
        engine.Paused = false;
        IsPlaying = true;
        Report(paused: false);
    }

    /// <summary>Back or ahead by <paramref name="seconds"/>, into the part before or after when it runs past this one's start or end.</summary>
    public void Skip(double seconds)
    {
        if (Book is not { } book) return;
        var target = CurrentTime + seconds;
        if (target < 0 && PartIndex > 0)
        {
            var previous = PartIndex - 1;
            Load(previous, Math.Max(book.PartDurations[previous] + target, 0), IsPlaying);
        }
        else if (target > Duration && Duration > 0 && PartIndex + 1 < book.Parts.Count)
        {
            Load(PartIndex + 1, target - Duration, IsPlaying);
        }
        else
        {
            Seek(target);
        }
    }

    /// <summary>Moves within the part playing.</summary>
    public void Seek(double seconds)
    {
        CurrentTime = Math.Max(0, Duration > 0 ? Math.Min(seconds, Duration) : seconds);
        engine.Seek(CurrentTime);
        Report(paused: !IsPlaying);
    }

    /// <summary>Moves to a point in the whole book, in whichever part it falls (the system's media controls give one).</summary>
    public void SeekInBook(double seconds)
    {
        if (Book is not { } book) return;
        var part = book.Parts.Count - 1;
        while (part > 0 && book.OffsetOfPart(part) > seconds) part--;
        var within = Math.Max(0, seconds - book.OffsetOfPart(part));
        if (part == PartIndex) Seek(within);
        else Load(part, within, IsPlaying);
    }

    public void JumpTo(AudiobookChapter chapter)
    {
        if (chapter.Part == PartIndex) Seek(chapter.Start);
        else Load(chapter.Part, chapter.Start, IsPlaying);
    }

    public void NextChapter()
    {
        if (CurrentChapter is { } current && Chapters.FirstOrDefault(c => c.Id == current.Id + 1) is { } next) JumpTo(next);
    }

    /// <summary>The start of this chapter, or the one before when just into it.</summary>
    public void PreviousChapter()
    {
        if (CurrentChapter is not { } current)
        {
            Seek(0);
            return;
        }
        var intoChapter = current.Part == PartIndex ? CurrentTime - current.Start : CurrentTime;
        if (intoChapter > 3 || current.Id == 0) JumpTo(current);
        else if (Chapters.FirstOrDefault(c => c.Id == current.Id - 1) is { } previous) JumpTo(previous);
    }

    public void SetRate(double rate)
    {
        Rate = rate;
        if (Book is { } book) AudiobookRates.SetRate(rate, book.Id);
        engine.Speed = rate;
    }

    /// <summary>Stops and lets go of the book, reporting where it got to.</summary>
    public void Stop()
    {
        if (Book is null) return;
        FinishCurrent();
        clock.Stop();
        engine.Stop();
        Book = null;
        PartIndex = 0;
        IsPlaying = false;
        CurrentTime = 0;
        Duration = 0;
        ErrorMessage = null;
    }

    // ---- Loading ----

    private void Load(int part, double seconds, bool playing)
    {
        if (client is null || Book is not { } book || part < 0 || part >= book.Parts.Count) return;
        FinishCurrent();
        PartIndex = part;
        playSessionId = Guid.NewGuid().ToString("N");
        hasStarted = false;
        CurrentTime = seconds;
        playWhenReady = playing;
        Duration = book.PartDurations[part];
        ErrorMessage = null;
        engine.Load(client.UniversalAudioUrl(book.Parts[part].Id, playSessionId, AppSettings.Current.MaxBitrate), seconds, playing);
        IsPlaying = playing;
        clock.Start();
    }

    private void Started()
    {
        if (hasStarted || Book is null) return;
        hasStarted = true;
        if (engine.Duration is > 0 and var length) Duration = length;
        CurrentTime = engine.Position ?? CurrentTime;
        lastReport = DateTimeOffset.UtcNow;
        Report(paused: !playWhenReady, start: true);
    }

    private void Tick()
    {
        if (!hasStarted) return;
        if (engine.Position is { } seconds) CurrentTime = seconds;
        if (DateTimeOffset.UtcNow - lastReport >= TimeSpan.FromSeconds(10))
        {
            lastReport = DateTimeOffset.UtcNow;
            Report(paused: !IsPlaying);
        }
    }

    /// <summary>Reports the part that's ending, so the server keeps its position.</summary>
    private void FinishCurrent()
    {
        if (!hasStarted || Book is not { } book || client is null || PartIndex >= book.Parts.Count) return;
        hasStarted = false;
        _ = client.ReportStoppedAsync(ReportFor(book, paused: true));
    }

    private void PartEnded()
    {
        if (Book is not { } book) return;
        if (PartIndex + 1 < book.Parts.Count)
        {
            Load(PartIndex + 1, 0, playing: true);
            return;
        }
        // The end of the book: report it finished and stop there.
        FinishCurrent();
        engine.Paused = true;
        IsPlaying = false;
        playWhenReady = false;
        CurrentTime = Duration;
    }

    private PlaybackReport ReportFor(Audiobook book, bool paused) => new()
    {
        ItemId = book.Parts[PartIndex].Id,
        PlaySessionId = playSessionId,
        PositionTicks = engine.Position is > 0 and var seconds ? (long)(seconds * 10_000_000) : 0,
        IsPaused = paused,
        PlayMethod = "DirectPlay",
        RunTimeTicks = book.Parts[PartIndex].RunTimeTicks,
    };

    private void Report(bool paused, bool start = false)
    {
        if (!hasStarted || Book is not { } book || client is null) return;
        var report = ReportFor(book, paused);
        _ = start ? client.ReportStartAsync(report) : client.ReportProgressAsync(report);
    }

    public void Dispose()
    {
        Stop();
        engine.Dispose();
    }
}
