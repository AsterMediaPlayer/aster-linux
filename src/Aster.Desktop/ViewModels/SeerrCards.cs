using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Jellyfin;
using Aster.Core.Seerr;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// A film or show from Seerr, as a poster: where it stands (Requested,
/// Available…) on it, or for a request, the request's state and who asked.
/// Opens its request page; one the library has opens the library's page.
/// </summary>
public sealed partial class SeerrCard(INavigator navigator, SeerrTitle title, string? badge = null, string? subtitle = null, SeerrRequest? request = null) : ICard
{
    public SeerrTitle Item => title;
    public SeerrRequest? Request => request;
    public string Key => request is { } asked ? $"request-{asked.Id}" : title.Key;
    public Uri? Image { get; } = SeerrClient.Image(title.PosterPath, "w342") is { } url ? new Uri(url) : null;
    public string Title => title.DisplayTitle;
    public string? Subtitle { get; } = subtitle ?? string.Join(" · ", new[] { title.Year, title.IsMovie ? "Film" : "Series" }.OfType<string>());
    public string? Badge { get; } = badge ?? title.Status.Label();
    public bool HasBadge => Badge is not null;
    public bool IsAvailable => title.Status == SeerrStatus.Available;

    [RelayCommand]
    private Task Open() => SeerrPages.OpenAsync(navigator, title, request);
}

/// <summary>A genre, studio or network: its picture (or logo) and name; opens its titles.</summary>
public sealed partial class SeerrTileCard(string key, string name, Uri? image, Uri? logo, Action open) : ICard
{
    public string Key => key;
    public string Name => name;
    public Uri? Image => image;
    public Uri? Logo => logo;
    public bool HasLogo => logo is not null;

    [RelayCommand]
    private void Open() => open();
}

public static class SeerrPages
{
    /// <summary>
    /// A title's page: the library's when the server has it (Seerr knows its
    /// Jellyfin id), else its Seerr page, to read about and request.
    /// </summary>
    public static async Task OpenAsync(INavigator navigator, SeerrTitle title, SeerrRequest? request = null)
    {
        // A request still waiting opens its Seerr page, for Approve, Decline or Cancel.
        if (request is { IsPending: true })
        {
            navigator.Push(new SeerrTitleViewModel(navigator, title, request));
            return;
        }
        if (title.MediaInfo?.JellyfinMediaId is { Length: > 0 } id && navigator.Jellyfin is { } jellyfin)
        {
            try
            {
                navigator.Open(await jellyfin.ItemAsync(id));
                return;
            }
            catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException) { }
        }
        navigator.Push(new SeerrTitleViewModel(navigator, title));
    }

    /// <summary>Whether any of the Jellyfin server's libraries has it in 4K; false without Jellyfin, or when the server can't say.</summary>
    public static async Task<bool> Has4kCopyAsync(INavigator navigator, string tmdbId, bool movie, string name)
    {
        if (navigator.Jellyfin is not { } jellyfin || name.Length == 0) return false;
        try { return await jellyfin.Has4kCopyAsync(tmdbId, movie, name); }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException or TaskCanceledException or System.Text.Json.JsonException) { return false; }
    }

    private static (JellyfinClient Server, DateTime At, IReadOnlySet<int> Ids)? uhdFilms;

    /// <summary>
    /// The TMDB ids of the films a library has in 4K, for Discover to leave
    /// out (nothing there to ask for); kept ten minutes. None without
    /// Jellyfin, or when the server can't say.
    /// </summary>
    public static async Task<IReadOnlySet<int>> Uhd4kFilmsAsync(INavigator navigator)
    {
        if (navigator.Jellyfin is not { } jellyfin) return new HashSet<int>();
        if (uhdFilms is { } kept && ReferenceEquals(kept.Server, jellyfin) && DateTime.UtcNow - kept.At < TimeSpan.FromMinutes(10)) return kept.Ids;
        try
        {
            var ids = await jellyfin.Uhd4kFilmsAsync();
            uhdFilms = (jellyfin, DateTime.UtcNow, ids);
            return ids;
        }
        catch (Exception error) when (error is JellyfinException or System.Net.Http.HttpRequestException or TimeoutException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new HashSet<int>();
        }
    }

    /// <summary>Discover's titles, but the films a library has in 4K.</summary>
    public static IEnumerable<SeerrTitle> Not4k(this IEnumerable<SeerrTitle> titles, IReadOnlySet<int> uhd) =>
        titles.Where(t => !(t.IsMovie && uhd.Contains(t.Id)));

    public static Uri? Url(string? url) => url is null ? null : new Uri(url);
}
