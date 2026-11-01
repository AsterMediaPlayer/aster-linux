using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// The Search tab: what's typed is looked for a moment after typing stops,
/// with movies and shows as a grid, then shelves of episodes, albums and
/// playlists, artists and songs.
/// </summary>
public sealed partial class SearchViewModel(INavigator navigator) : ViewModelBase
{
    private CancellationTokenSource? searching;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShows))]
    public partial IReadOnlyList<ItemCard> Shows { get; set; } = [];

    [ObservableProperty] public partial IReadOnlyList<ItemCard> Episodes { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ItemCard> Albums { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ItemCard> Artists { get; set; } = [];
    [ObservableProperty] public partial IReadOnlyList<ItemCard> Songs { get; set; } = [];

    /// <summary>With Seerr: films and shows it has for the search that the library hasn't, to request.</summary>
    [ObservableProperty] public partial IReadOnlyList<SeerrCard> Requestable { get; set; } = [];

    public bool HasShows => Shows.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? MessageTitle { get; set; } = "Search";

    [ObservableProperty]
    public partial string? MessageDetail { get; set; } = "Find movies, TV shows, episodes and music in your library.";

    public bool HasMessage => MessageTitle is not null;

    [ObservableProperty] public partial bool IsSearching { get; set; }

    /// <summary>The tab was chosen: the search box takes the keys.</summary>
    public event Action? FocusRequested;

    public void RequestFocus() => FocusRequested?.Invoke();

    partial void OnQueryChanged(string value) => _ = SearchAsync(value.Trim());

    private async Task SearchAsync(string term)
    {
        searching?.Cancel();
        if (term.Length == 0)
        {
            Show([]);
            Requestable = [];
            ShowMessage("Search", "Find movies, TV shows, episodes and music in your library.");
            return;
        }
        var cancel = searching = new CancellationTokenSource();
        try
        {
            await Task.Delay(350, cancel.Token);
            IsSearching = true;
            // Seerr's alongside, so the library's results needn't wait for it.
            var requestable = RequestableAsync(term, cancel.Token);
            var results = await navigator.Client.SearchAsync(term, ct: cancel.Token);
            if (cancel.IsCancellationRequested) return;
            Show(results);
            if (results.Count == 0) ShowMessage("No Results", $"Nothing in the library matched “{term}”.");
            else ShowMessage(null, null);
            Requestable = await requestable;
            if (cancel.IsCancellationRequested) return;
            if (results.Count == 0 && Requestable.Count > 0) ShowMessage("Not in the Library", $"Nothing in the library matched “{term}”. Seerr has these to request.");
        }
        catch (OperationCanceledException) { return; }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException)
        {
            if (error is JellyfinException { StatusCode: 401 }) await navigator.SessionExpiredAsync();
            Show([]);
            ShowMessage("Couldn't Search", error.Message);
        }
        if (searching == cancel) IsSearching = false;
    }

    /// <summary>Seerr's films and shows for the search, less what the library has; none without Seerr, or when it fails.</summary>
    private async Task<IReadOnlyList<SeerrCard>> RequestableAsync(string term, CancellationToken ct)
    {
        if (navigator.Seerr.Account is not { } account) return [];
        try
        {
            var found = await Aster.Core.Seerr.SeerrClient.SearchAsync(term, account, ct);
            return found.Where(t => t.Status != Aster.Core.Seerr.SeerrStatus.Available).DistinctBy(t => t.Key).Take(20)
                .Select(t => new SeerrCard(navigator, t)).ToList();
        }
        catch (Aster.Core.Seerr.SeerrException error) when (error.Failure == Aster.Core.Seerr.SeerrFailure.SignedOut)
        {
            navigator.Seerr.SessionEnded();
            return [];
        }
        catch (Exception error) when (error is Aster.Core.Seerr.SeerrException or System.Net.Http.HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private void ShowMessage(string? title, string? detail)
    {
        MessageTitle = title;
        MessageDetail = detail;
    }

    private void Show(IReadOnlyList<BaseItem> results)
    {
        Shows = results.Where(i => i.Kind is ItemKind.Movie or ItemKind.Series).Select(i => ItemCard.Poster(navigator, i)).ToList();
        Episodes = results.Where(i => i.Kind == ItemKind.Episode).Select(i => ItemCard.Landscape(navigator, i)).ToList();
        Albums = results.Where(i => i.Kind is ItemKind.MusicAlbum or ItemKind.Playlist).Select(i => ItemCard.Square(navigator, i)).ToList();
        Artists = results.Where(i => i.Kind == ItemKind.MusicArtist).Select(i => ItemCard.Round(navigator, i)).ToList();
        // A song plays the songs found, from it.
        var songs = results.Where(i => i.Kind == ItemKind.Audio).ToList();
        Songs = songs.Select((song, index) => ItemCard.Square(navigator, song,
            () => navigator.Audio.Music.Play(songs, index, shuffled: false, navigator.JellyfinOnly))).ToList();
    }
}
