using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Seerr;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

public partial class DetailViewModel
{
    /// <summary>
    /// The film or show as Seerr has it, when its 4K copy can be asked for:
    /// the library has it (in HD, say), the one signed in to Seerr may ask
    /// for 4K, Seerr has a 4K server for it, no library has it in 4K, and
    /// Seerr's 4K copy isn't there or on its way.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRequest4k))]
    public partial SeerrTitle? Seerr4k { get; private set; }

    public bool CanRequest4k => Seerr4k is not null;

    /// <summary>Its Seerr page, set to 4K, to choose and send the request from.</summary>
    [RelayCommand]
    private void Request4k()
    {
        if (Seerr4k is { } title) navigator.Push(new SeerrTitleViewModel(navigator, title, prefer4k: true));
    }

    /// <summary>Its place on the person's linked watchlist (Simkl's Plan to Watch), for a film or show.</summary>
    public WatchlistModel Watchlist { get; }

    private async Task LoadWatchlistAsync(BaseItem item)
    {
        if (item.Kind is not (ItemKind.Movie or ItemKind.Series)) return;
        var anime = item.Kind == ItemKind.Series && (await navigator.Anime).ItemIds.Contains(item.Id);
        await Watchlist.LoadAsync(item.Kind == ItemKind.Movie ? "movie" : "tv", anime,
            Aster.Core.Tracking.AsterTracking.TrackingIds(item.ProviderIds), item.Name, item.ProductionYear);
    }

    /// <summary>Whether there's a 4K copy to ask for (by the title's TMDB id); any fault leaves the button out.</summary>
    private async Task LoadSeerr4kAsync(BaseItem item)
    {
        if (navigator.Seerr.Account is not { } account || item.Kind is not (ItemKind.Movie or ItemKind.Series)
            || item.ProviderIds?.FirstOrDefault(p => p.Key.Equals("Tmdb", StringComparison.OrdinalIgnoreCase)).Value is not { } raw
            || !int.TryParse(raw, out var tmdb))
        {
            Seerr4k = null;
            return;
        }
        var movie = item.Kind == ItemKind.Movie;
        try
        {
            var me = await SeerrClient.MeAsync(account);
            if (!me.Requests4k(movie))
            {
                Seerr4k = null;
                return;
            }
            // Already in 4K, in this library or another: nothing to ask for.
            if (await SeerrPages.Has4kCopyAsync(navigator, raw, movie, item.Name ?? ""))
            {
                Seerr4k = null;
                return;
            }
            // Whoever manages requests sees the servers; anyone else, whether 4K is on.
            var offered = me.ManagesRequests
                ? (await SeerrClient.ServicesAsync(movie ? SeerrKind.Movie : SeerrKind.Tv, account)).Any(s => s.Is4k == true)
                : await SeerrClient.SignInOptionsAsync(account.Url) is var settings && (movie ? settings.Movie4kEnabled : settings.Series4kEnabled) == true;
            var title = new SeerrTitle { Id = tmdb, MediaType = movie ? "movie" : "tv" };
            var details = offered ? await SeerrClient.DetailsAsync(title, account) : null;
            Seerr4k = details is not null && (details.MediaInfo?.Status4k ?? SeerrStatus.Unknown).CanRequest()
                ? title with
                {
                    Title = details.Title, Name = details.Name, ReleaseDate = details.ReleaseDate, FirstAirDate = details.FirstAirDate,
                    PosterPath = details.PosterPath, BackdropPath = details.BackdropPath, Overview = details.Overview, MediaInfo = details.MediaInfo,
                }
                : null;
        }
        catch (SeerrException error) when (error.Failure == SeerrFailure.SignedOut) { navigator.Seerr.SessionEnded(); Seerr4k = null; }
        catch (Exception error) when (error is SeerrException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { Seerr4k = null; }
    }
}
