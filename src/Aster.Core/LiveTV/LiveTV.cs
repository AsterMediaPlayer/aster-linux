using System.Globalization;
using Aster.Core.Channels;
using Aster.Core.Jellyfin;

namespace Aster.Core.LiveTV;

/// <summary>Times and days for Live TV, in the viewer's time zone. The Apple app's LiveTVFormat.</summary>
public static class LiveTVFormat
{
    /// <summary>"7:30 PM – 8:00 PM", or the start alone.</summary>
    public static string TimeRange(DateTimeOffset? start, DateTimeOffset? end, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        if (start is not { } from) return "";
        var text = Time(from, zone, culture);
        return end is { } to ? $"{text} – {Time(to, zone, culture)}" : text;
    }

    /// <summary>"Today", "Tomorrow", "Yesterday", else "Thursday, Oct 15".</summary>
    public static string DayLabel(DateTimeOffset date, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        var day = Local(date, zone).Date;
        return (day - Local(now, zone).Date).Days switch
        {
            0 => "Today",
            1 => "Tomorrow",
            -1 => "Yesterday",
            _ => day.ToString("dddd, MMM d", culture ?? CultureInfo.CurrentCulture),
        };
    }

    /// <summary>"Tomorrow, 7:30 PM – 8:00 PM".</summary>
    public static string Airing(DateTimeOffset? start, DateTimeOffset? end, DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        start is { } from ? $"{DayLabel(from, now, zone, culture)}, {TimeRange(start, end, zone, culture)}" : "";

    /// <summary>
    /// "7:30 PM", in the culture's short time. Linux's ICU puts a narrow
    /// no-break space before "PM" where Windows puts a plain one; it's a
    /// plain one on both.
    /// </summary>
    public static string Time(DateTimeOffset date, TimeZoneInfo? zone = null, CultureInfo? culture = null) =>
        Local(date, zone).ToString("t", culture ?? CultureInfo.CurrentCulture).Replace(' ', ' ').Replace(' ', ' ');

    private static DateTime Local(DateTimeOffset date, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTime(date, zone ?? TimeZoneInfo.Local).DateTime;
}

/// <summary>The guide's arithmetic: its channels' order and its window of time.</summary>
public static class LiveGuide
{
    /// <summary>The guide's order: favourites first, then by channel number, then name.</summary>
    public static IReadOnlyList<BaseItem> Order(IEnumerable<BaseItem> channels) => channels
        .OrderByDescending(c => c.UserData?.IsFavorite == true)
        .ThenBy(NumberOf)
        .ThenBy(c => c.DisplayTitle, StringComparer.CurrentCulture)
        .ToList();

    /// <summary>Live TV's favourites, a group of their own at the front once channels have groups.</summary>
    public const string FavoritesGroup = "Favorites";

    /// <summary>
    /// The guide's order: favourites first, then each group in order of its
    /// lowest channel number (its channels by number), the ungrouped under
    /// Other. Without groups, favourites then everything by number, as ever.
    /// <c>Grouping</c> is each channel's own group, for the guide's filter;
    /// <c>Listed</c> has the favourites as a group of their own, as they
    /// come first, for the player's row.
    /// </summary>
    public static (IReadOnlyList<BaseItem> Channels, ChannelGrouping? Grouping, ChannelGrouping? Listed) Arranged(
        IEnumerable<BaseItem> channels, IReadOnlyDictionary<string, string> groups)
    {
        var all = channels.ToList();
        var byNumber = all.OrderBy(NumberOf).ThenBy(c => c.DisplayTitle, StringComparer.CurrentCulture).ToList();
        if (ChannelGrouping.Make(byNumber, c => c.Id, c => groups.GetValueOrDefault(c.Id)) is not var (grouping, ordered))
        {
            return (Order(all), null, null);
        }
        static bool IsFavorite(BaseItem channel) => channel.UserData?.IsFavorite == true;
        var favorites = byNumber.Where(IsFavorite).ToList();
        var listedGroups = new Dictionary<string, string>(grouping.Groups);
        foreach (var favorite in favorites) listedGroups[favorite.Id] = FavoritesGroup;
        var listed = new ChannelGrouping(listedGroups, (favorites.Count > 0 ? [FavoritesGroup] : new List<string>()).Concat(grouping.Names).ToList());
        return ([.. favorites, .. ordered.Where(c => !IsFavorite(c))], grouping, listed);
    }

    private static double NumberOf(BaseItem channel) =>
        double.TryParse(channel.ChannelNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : double.MaxValue;

    /// <summary>The half hour <paramref name="date"/> falls in, where the guide starts.</summary>
    public static DateTimeOffset FloorToHalfHour(DateTimeOffset date)
    {
        var ticks = TimeSpan.FromMinutes(30).Ticks;
        var utc = date.UtcTicks;
        return new DateTimeOffset(utc - utc % ticks, TimeSpan.Zero);
    }

    /// <summary>Keeps a window of <paramref name="minutes"/> starting at <paramref name="start"/> inside the guide.</summary>
    public static DateTimeOffset Clamp(DateTimeOffset start, double minutes, DateTimeOffset guideStart, DateTimeOffset guideEnd)
    {
        var latest = guideEnd.AddMinutes(-minutes);
        if (latest < guideStart) latest = guideStart;
        return start < guideStart ? guideStart : start > latest ? latest : start;
    }

    /// <summary>
    /// A programme's span on the guide: clipped to the guide's start, and at
    /// least five minutes long; half an hour when the listing has no end.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Slot(BaseItem program, DateTimeOffset guideStart)
    {
        var start = program.Start is { } s && s > guideStart ? s : guideStart;
        var end = program.End ?? start.AddMinutes(30);
        return (start, end < start.AddMinutes(5) ? start.AddMinutes(5) : end);
    }
}

public static class LiveTVImages
{
    extension(JellyfinClient client)
    {
        /// <summary>A programme's own picture, else its channel's logo.</summary>
        public Uri? ProgramImageUrl(BaseItem program, int width = 800)
        {
            if (program.ImageTags?.GetValueOrDefault("Primary") is { } primary)
                return client.ImageUrl(program.Id, ImageKind.Primary, primary, maxWidth: width);
            if (program.ImageTags?.GetValueOrDefault("Thumb") is { } thumb)
                return client.ImageUrl(program.Id, ImageKind.Thumb, thumb, maxWidth: width);
            if (program.ChannelId is { } channel && program.ChannelPrimaryImageTag is { } logo)
                return client.ImageUrl(channel, ImageKind.Primary, logo, maxWidth: width);
            return null;
        }

        public Uri? ChannelLogoUrl(BaseItem channel, int width = 400) =>
            channel.ImageTags?.GetValueOrDefault("Primary") is { } tag ? client.ImageUrl(channel.Id, ImageKind.Primary, tag, maxWidth: width) : null;
    }
}
