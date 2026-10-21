using System;
using System.ComponentModel;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.Audio;

/// <summary>
/// The song or book playing, whichever page is showing; only one ever is.
/// Feeds the bar at the foot of the sidebar (the Apple app's audio
/// accessory): play/pause there, the rest opens the full player, ✕ stops it.
/// </summary>
public sealed partial class AudioSession : ObservableObject, IDisposable
{
    private readonly JellyfinClient client;

    public AudioSession(JellyfinClient client)
    {
        this.client = client;
        // Starting one stops the other.
        Music.Starting += () => Books.Stop();
        Books.Starting += () => Music.Stop();
        Music.PropertyChanged += Changed;
        Books.PropertyChanged += Changed;
    }

    public MusicPlayer Music { get; } = new();
    public AudiobookPlayer Books { get; } = new();

    /// <summary>Whether a song or a book is loaded (playing or paused).</summary>
    public bool IsLoaded => Music.Current is not null || Books.Book is not null;

    public bool IsPlaying => Music.IsPlaying || Books.IsPlaying;

    /// <summary>What the bar shows: the book and its chapter, or the song and its artist.</summary>
    public string? Title => Books.Book?.Title ?? Music.Current?.DisplayTitle;

    public string? Detail => Books.Book is { } book
        ? Books.CurrentChapter?.Title ?? book.Author
        : Music.Current is { } track ? track.ArtistText ?? track.Album : null;

    public Uri? Artwork => Books.Book is { } book
        ? client.PosterUrl(book.Cover, width: 200)
        : Music.Current is { } track ? client.PosterUrl(track, width: 200) : null;

    /// <summary>Whether the full player is up, over the window.</summary>
    [ObservableProperty] public partial bool IsNowPlayingShown { get; set; }

    /// <summary>Which full player: the book's, when a book is loaded.</summary>
    public bool ShowsBookPlayer => Books.Book is not null;

    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(Artwork));
        OnPropertyChanged(nameof(ShowsBookPlayer));
        if (!IsLoaded) IsNowPlayingShown = false;
    }

    /// <summary>Pauses or resumes whichever is loaded.</summary>
    [RelayCommand]
    public void Toggle()
    {
        if (Books.Book is not null) Books.TogglePlayPause();
        else if (Music.Current is not null) Music.TogglePlayPause();
    }

    /// <summary>Ends the song or book: the player closes and the bar goes away. A book's place is saved first.</summary>
    [RelayCommand]
    public void Stop()
    {
        Books.Stop();
        Music.Stop();
    }

    /// <summary>Pauses the song or book as a video starts, so the two never play over each other. It stays loaded, to carry on from the bar.</summary>
    public void Pause()
    {
        if (Books.IsPlaying) Books.Pause();
        if (Music.IsPlaying) Music.Pause();
    }

    [RelayCommand] private void ShowPlayer() => IsNowPlayingShown = IsLoaded;
    [RelayCommand] private void HidePlayer() => IsNowPlayingShown = false;

    public void Dispose()
    {
        Music.Dispose();
        Books.Dispose();
    }
}
