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
public sealed partial class UpcomingCard(INavigator navigator, BaseItem episode) : ICard
{
    public string Key => episode.Id;
    public Uri? Image { get; } = navigator.Client.BackdropUrl(episode, width: 800);
    public Uri? Logo { get; } = navigator.Client.LogoUrl(episode, width: 400);
    public string Title => episode.SeriesName ?? episode.DisplayTitle;
    public string EpisodeLine { get; } = UpcomingSchedule.EpisodeLine(episode);
    public bool IsAvailable { get; } = UpcomingSchedule.IsAvailable(episode);
    public string AccessibleName => string.Join(", ", new[] { Title, EpisodeLine, IsAvailable ? "Available" : null }.OfType<string>());

    [RelayCommand]
    private void Open() => navigator.Open(UpcomingSchedule.Destination(episode));
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
        bool showsAnime, bool showsTV, Action<bool, bool> changed)
    {
        if (navigator.Jellyfin is not { } jellyfin || !jellyfin.Can(ServerCapabilities.Upcoming))
            return [heading, new StatusRow("Nothing coming up", "Upcoming episodes need a Jellyfin or Emby server.")];
        var catalog = await navigator.Anime;
        var all = UpcomingSchedule.Days(await jellyfin.UpcomingAsync());
        var rows = new List<BrowseRow> { heading };
        // The switches, when the library has both kinds of show (even if this week has only one kind coming).
        var animeLibraries = catalog.Libraries.Select(l => l.Id).ToHashSet();
        var hasBoth = animeLibraries.Count > 0 && (await navigator.Libraries).Any(l => l.CollectionType == "tvshows" && !animeLibraries.Contains(l.Id));
        if (hasBoth)
            rows.Add(new FilterRow(
            [
                new FilterChip(showsAnime ? "✓  Anime" : "Anime", showsAnime, new RelayCommand(() => changed(!showsAnime, showsTV))),
                new FilterChip(showsTV ? "✓  TV Shows" : "TV Shows", showsTV, new RelayCommand(() => changed(showsAnime, !showsTV))),
            ]));
        var now = DateTime.Now;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        foreach (var (day, episodes) in all)
        {
            var shown = episodes.Where(e => catalog.ItemIds.Contains(e.SeriesId ?? "") ? showsAnime : showsTV).ToList();
            if (shown.Count == 0) continue;
            var away = (day.Date - now.Date).Days;
            var relative = away switch { 0 => "Today", 1 => "Tomorrow", < 7 => $"In {away} days", _ => $"In {away / 7} week{(away >= 14 ? "s" : "")}" };
            rows.Add(new AgendaDayRow(day.ToString("ddd", culture).ToUpperInvariant(), day.Day.ToString(culture), day.ToString("MMMM", culture), relative,
                shown.Select(e => new UpcomingCard(navigator, e)).ToList()));
        }
        if (rows.Count == (hasBoth ? 2 : 1))
            rows.Add(new StatusRow("Nothing coming up", all.Count == 0
                ? "Episodes of shows in your library that air soon show up here."
                : !showsAnime && !showsTV ? "Turn on Anime or TV Shows above." : !showsAnime ? "No TV shows have episodes coming." : "No anime has episodes coming."));
        return rows;
    }
}
