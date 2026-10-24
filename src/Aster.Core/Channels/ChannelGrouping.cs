// Channels in named groups, like a cable guide's: Kids, Anime, Movies. A
// group goes in order of its lowest channel number, its channels by number
// within it, and channels without one go under "Other". Until some channel
// has a group there are none, and everything stays as it was. Used by the
// Channels tab (the aster:group= tag) and Live TV (groups the Aster Channels
// plugin keeps). A port of the Apple app's ChannelGroups.swift.

namespace Aster.Core.Channels;

public sealed record ChannelGrouping(IReadOnlyDictionary<string, string> Groups, IReadOnlyList<string> Names)
{
    /// <summary>The group for channels that have none, once some channel has one.</summary>
    public const string Other = "Other";

    public string? GroupOf(string channelId) => Groups.GetValueOrDefault(channelId);

    /// <summary>
    /// The channels in group order (a group's channels kept in the order
    /// given, which should be by number); null when no channel has a group.
    /// <paramref name="groupOf"/> is each channel's own group, or null.
    /// </summary>
    public static (ChannelGrouping Grouping, IReadOnlyList<T> Ordered)? Make<T>(IReadOnlyList<T> channels, Func<T, string> id, Func<T, string?> groupOf)
    {
        if (!channels.Any(c => groupOf(c) is not null)) return null;
        var groups = new Dictionary<string, string>();
        var names = new List<string>();
        foreach (var channel in channels)
        {
            var name = groupOf(channel) ?? Other;
            groups[id(channel)] = name;
            // Channels come by number, so a group's first channel is its lowest.
            if (!names.Contains(name)) names.Add(name);
        }
        // OrderBy is stable, so each group keeps its channels' order.
        var ordered = channels.OrderBy(c => names.IndexOf(groups[id(c)])).ToList();
        return (new ChannelGrouping(groups, names), ordered);
    }

    /// <summary>The ids, in the order given, of the channels in <paramref name="group"/>.</summary>
    public IReadOnlyList<string> Channels(IEnumerable<string> ids, string group) =>
        ids.Where(i => Groups.GetValueOrDefault(i) == group).ToList();
}
