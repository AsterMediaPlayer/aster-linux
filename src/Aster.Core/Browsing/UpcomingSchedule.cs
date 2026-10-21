using System.Globalization;
using Aster.Core.Jellyfin;

namespace Aster.Core.Browsing;

/// <summary>
/// Dates and wording for the Upcoming tab's episodes. Most upcoming episodes
/// are placeholders Jellyfin makes from the show's schedule, with no
/// picture or description of their own and a date but no real air time, so
/// the tab speaks in days ("Tomorrow", "Thursday"), never clock times. A
/// port of the Apple app's UpcomingSchedule.
/// </summary>
public static class UpcomingSchedule
{
    /// <summary>
    /// The day an episode airs, in <paramref name="zone"/> (the computer's,
    /// by default). Jellyfin gives the air date in UTC with seven decimal
    /// places; the fraction is dropped, as the Apple app does.
    /// </summary>
    public static DateTime? AirDate(BaseItem episode, TimeZoneInfo? zone = null)
    {
        if (episode.PremiereDate is not { Length: >= 19 } text) return null;
        if (!DateTime.TryParseExact(text[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)) return null;
        return TimeZoneInfo.ConvertTimeFromUtc(utc, zone ?? TimeZoneInfo.Local);
    }

    /// <summary>Episodes in airing order, gathered by the local day they air.</summary>
    public static IReadOnlyList<(DateTime Day, IReadOnlyList<BaseItem> Episodes)> Days(IEnumerable<BaseItem> episodes, TimeZoneInfo? zone = null)
    {
        var dated = episodes
            .Select(e => (Date: AirDate(e, zone), Episode: e))
            .Where(e => e.Date is not null)
            .OrderBy(e => e.Date)
            .ThenBy(e => e.Episode.SeriesName ?? "", StringComparer.Ordinal)
            .ThenBy(e => e.Episode.ParentIndexNumber ?? 0)
            .ThenBy(e => e.Episode.IndexNumber ?? 0);
        var days = new List<(DateTime Day, List<BaseItem> Episodes)>();
        foreach (var (date, episode) in dated)
        {
            var day = date!.Value.Date;
            if (days.Count > 0 && days[^1].Day == day) days[^1].Episodes.Add(episode);
            else days.Add((day, [episode]));
        }
        return days.Select(d => (d.Day, (IReadOnlyList<BaseItem>)d.Episodes)).ToList();
    }

    /// <summary>"Today", "Tomorrow", "In 3 days", then "In 2 weeks".</summary>
    public static string Countdown(DateTime day, DateTime now)
    {
        var days = (day.Date - now.Date).Days;
        return days switch
        {
            < 1 => "Today",
            1 => "Tomorrow",
            <= 6 => $"In {days} days",
            _ => days / 7 == 1 ? "In 1 week" : $"In {days / 7} weeks",
        };
    }

    /// <summary>A day's heading: "Tomorrow · Thursday, October 15", or just the weekday and date further out.</summary>
    public static string Heading(DateTime day, DateTime now, CultureInfo? culture = null)
    {
        var date = day.ToString("dddd, MMMM d", culture ?? CultureInfo.CurrentCulture);
        return (day.Date - now.Date).Days switch
        {
            0 => $"Today · {date}",
            1 => $"Tomorrow · {date}",
            _ => date,
        };
    }

    /// <summary>"S2 · E3 · The Blade". A placeholder's title ("Episode 3") only repeats the number, so it's left out.</summary>
    public static string EpisodeLine(BaseItem episode)
    {
        var parts = new List<string>();
        if (episode.ParentIndexNumber is { } season) parts.Add($"S{season}");
        if (episode.IndexNumber is { } number) parts.Add($"E{number}");
        if (!string.IsNullOrEmpty(episode.Name) && episode.Name != $"Episode {episode.IndexNumber ?? -1}") parts.Add(episode.Name);
        return string.Join(" · ", parts);
    }

    /// <summary>Whether the episode's file is already on the server (out early).</summary>
    public static bool IsAvailable(BaseItem episode) => episode.LocationType == "FileSystem";

    /// <summary>What a card or the banner opens: the episode when it can be watched, else its show.</summary>
    public static BaseItem Destination(BaseItem episode) =>
        IsAvailable(episode) || episode.SeriesId is not { } seriesId
            ? episode
            : new BaseItem { Id = seriesId, Name = episode.SeriesName, Type = "Series" };
}
