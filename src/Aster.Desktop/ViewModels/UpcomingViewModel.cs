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
/// Discover's Upcoming section: episodes of shows in the library that air
/// soon. The next to air fill the banner with a countdown; below, a row of
/// cards for each day. A port of the Apple app's UpcomingView.
/// </summary>
public static class UpcomingRows
{
    /// <summary>The banner Upcoming fills, for a page to keep.</summary>
    public static HeroModel Hero(INavigator navigator) => new(navigator,
        open: episode => navigator.Open(UpcomingSchedule.Destination(episode)),
        play: episode => navigator.Open(UpcomingSchedule.Destination(episode), playOnOpen: UpcomingSchedule.IsAvailable(episode)),
        tag: episode => UpcomingSchedule.AirDate(episode) is { } date ? UpcomingSchedule.Countdown(date, DateTime.Now) : null,
        action: episode => UpcomingSchedule.IsAvailable(episode) ? new HeroAction("Watch Now", "\uE768") : new HeroAction("Go to Show", "\uE7F4"));

    /// <summary>The section's rows: the banner, then a row for each day; or a note when nothing's coming.</summary>
    public static async Task<IReadOnlyList<BrowseRow>> LoadAsync(INavigator navigator, HeroModel hero, BrowsePageViewModel page, BrowseRow heading)
    {
        if (navigator.Jellyfin is not { } jellyfin || !jellyfin.Can(ServerCapabilities.Upcoming))
            return [heading, new StatusRow("Nothing coming up", "Upcoming episodes need a Jellyfin or Emby server.")];
        var days = UpcomingSchedule.Days(await jellyfin.UpcomingAsync());
        if (days.Count == 0)
            return [heading, new StatusRow("Nothing coming up", "Episodes of shows in your library that air soon show up here.")];
        // Every upcoming episode, soonest first; the banner keeps the first 8.
        hero.Show(days.SelectMany(d => d.Episodes));
        var now = DateTime.Now;
        var rows = new List<BrowseRow> { heading, new HeroRow(hero, page) };
        rows.AddRange(days.Select(d => new ShelfRow(UpcomingSchedule.Heading(d.Day, now),
            d.Episodes.Select(e => (object)new UpcomingCard(navigator, e)).ToList(), CardStyle.Upcoming)));
        return rows;
    }
}
