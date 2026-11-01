using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aster.Core.Browsing;
using Aster.Core.Jellyfin;
using Aster.Core.Server;
using CommunityToolkit.Mvvm.Input;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// One upcoming episode: the show's picture, its logo or name, and the
/// episode, marked when its file is already on the server. Opens the
/// episode when it can be watched, else its show.
/// </summary>
public sealed class UpcomingCard(INavigator navigator, BaseItem episode) : AgendaEntry
{
    public override string Key => episode.Id;
    public override Uri? Image { get; } = navigator.Client.BackdropUrl(episode, width: 800);
    public override Uri? Logo { get; } = navigator.Client.LogoUrl(episode, width: 400);
    public override string Title => episode.SeriesName ?? episode.DisplayTitle;
    public override string EpisodeLine { get; } = UpcomingSchedule.EpisodeLine(episode);
    public override bool IsAvailable { get; } = UpcomingSchedule.IsAvailable(episode);
    public override IRelayCommand OpenCommand { get; } = new RelayCommand(() => navigator.Open(UpcomingSchedule.Destination(episode)));
}

/// <summary>Something on a day of Upcoming's agenda: an episode, or a film the viewer asked for.</summary>
public abstract class AgendaEntry : ICard
{
    public abstract string Key { get; }
    public abstract Uri? Image { get; }
    public abstract Uri? Logo { get; }
    public abstract string Title { get; }
    /// <summary>"S2 · E5 · Title", or a film's "Digital Release".</summary>
    public abstract string EpisodeLine { get; }
    public abstract bool IsAvailable { get; }
    public abstract IRelayCommand OpenCommand { get; }
    public string AccessibleName => string.Join(", ", new[] { Title, EpisodeLine, IsAvailable ? "Available" : null }.OfType<string>());
}

/// <summary>A film the viewer asked for through Seerr, on the day it arrives: "Digital Release", "On Disc" or "In Theaters".</summary>
public sealed class ComingFilmCard(INavigator navigator, Aster.Core.Seerr.SeerrComingFilm film) : AgendaEntry
{
    public override string Key => $"film-{film.Title.Id}";
    public override Uri? Image { get; } = SeerrPages.Url(Aster.Core.Seerr.SeerrClient.Image(film.Title.BackdropPath ?? film.Title.PosterPath, "w780"));
    public override Uri? Logo => null;
    public override string Title => film.Title.DisplayTitle;
    public override string EpisodeLine => film.How;
    public override bool IsAvailable => false;
    public override IRelayCommand OpenCommand { get; } = new AsyncRelayCommand(() => SeerrPages.OpenAsync(navigator, film.Title));
}

/// <summary>
/// Discover's Upcoming section as the Apple TV app shows it: an agenda.
/// Each day down the side (weekday, the date big, the month, how far off),
/// its episodes beside it as wide pictures; the focused show's picture
/// softly behind the page; Anime and other shows each switched off at the
/// top when the library has both. A port of the Apple app's UpcomingAgenda.
/// </summary>
public static class UpcomingAgenda
{
    public static async Task<IReadOnlyList<BrowseRow>> LoadAsync(INavigator navigator, BrowseRow heading,
        bool showsAnime, bool showsTV, bool showsFilms, Action<bool, bool, bool> changed)
    {
        // With Seerr: films the viewer asked for, approved, not out yet, on the day they arrive.
        var films = ComingFilmsAsync(navigator);
        if (navigator.Jellyfin is not { } jellyfin || !jellyfin.Can(ServerCapabilities.Upcoming))
            return [heading, new StatusRow("Nothing coming up", "Upcoming episodes need a Jellyfin or Emby server.")];
        var catalog = await navigator.Anime;
        var all = UpcomingSchedule.Days(await jellyfin.UpcomingAsync());
        var rows = new List<BrowseRow> { heading };
        // The switches, when the library has both kinds of show (even if this week has only one kind coming).
        var animeLibraries = catalog.Libraries.Select(l => l.Id).ToHashSet();
        var hasBoth = animeLibraries.Count > 0 && (await navigator.Libraries).Any(l => l.CollectionType == "tvshows" && !animeLibraries.Contains(l.Id));
        var coming = await films;
        var chips = new List<FilterChip>();
        if (hasBoth)
        {
            chips.Add(new FilterChip(showsAnime ? "✓  Anime" : "Anime", showsAnime, new RelayCommand(() => changed(!showsAnime, showsTV, showsFilms))));
            chips.Add(new FilterChip(showsTV ? "✓  TV Shows" : "TV Shows", showsTV, new RelayCommand(() => changed(showsAnime, !showsTV, showsFilms))));
        }
        if (coming.Count > 0 && chips.Count > 0)
            chips.Add(new FilterChip(showsFilms ? "✓  Movies" : "Movies", showsFilms, new RelayCommand(() => changed(showsAnime, showsTV, !showsFilms))));
        if (chips.Count > 0) rows.Add(new FilterRow(chips));
        var now = DateTime.Now;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        // Each day's episodes, then its films.
        var byDay = all.ToDictionary(d => d.Day.Date, d => d.Episodes
            .Where(e => catalog.ItemIds.Contains(e.SeriesId ?? "") ? showsAnime : showsTV)
            .Select(e => (AgendaEntry)new UpcomingCard(navigator, e)).ToList());
        if (showsFilms)
            foreach (var film in coming)
            {
                var day = film.Date.LocalDateTime.Date;
                if (!byDay.TryGetValue(day, out var entries)) byDay[day] = entries = [];
                entries.Add(new ComingFilmCard(navigator, film));
            }
        foreach (var (day, shown) in byDay.OrderBy(d => d.Key).Select(d => (d.Key, d.Value)))
        {
            if (shown.Count == 0) continue;
            var away = (day.Date - now.Date).Days;
            var relative = away switch { 0 => "Today", 1 => "Tomorrow", < 7 => $"In {away} days", _ => $"In {away / 7} week{(away >= 14 ? "s" : "")}" };
            rows.Add(new AgendaDayRow(day.ToString("ddd", culture).ToUpperInvariant(), day.Day.ToString(culture), day.ToString("MMMM", culture), relative, shown));
        }
        if (rows.Count == (chips.Count > 0 ? 2 : 1))
            rows.Add(new StatusRow("Nothing coming up", all.Count == 0 && coming.Count == 0
                ? "Episodes of shows in your library that air soon show up here."
                : !showsAnime && !showsTV ? "Turn on Anime or TV Shows above." : !showsAnime ? "No TV shows have episodes coming." : "No anime has episodes coming."));
        return rows;
    }

    private static async Task<IReadOnlyList<Aster.Core.Seerr.SeerrComingFilm>> ComingFilmsAsync(INavigator navigator)
    {
        if (navigator.Seerr.Account is not { } account) return [];
        try { return await Aster.Core.Seerr.SeerrClient.ComingFilmsAsync(account); }
        catch (Exception error) when (error is Aster.Core.Seerr.SeerrException or System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }
}
