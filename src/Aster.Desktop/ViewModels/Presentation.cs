using System.Globalization;
using System.Linq;
using Aster.Core.Jellyfin;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// "2019 · PG-13 · 2 hr 9 min · Action, Adventure", with its quality badge
/// and star rating apart so they can be drawn as such.
/// </summary>
public sealed record MetadataLine(string Text, string? Badge, string? Rating)
{
    /// <param name="showsUnwatched">A show's page: how many episodes are left to watch, after the years.</param>
    public static MetadataLine For(BaseItem item, bool showsUnwatched = false)
    {
        var parts = item.MetadataLine.ToList();
        if (showsUnwatched && item.Kind == ItemKind.Series && item.UserData?.UnplayedItemCount is > 0 and var unplayed)
        {
            parts.Insert(item.YearText is null ? 0 : 1, $"{unplayed} unwatched");
        }
        var badge = item.Is4K ? "4K" : item.IsHD ? "HD" : null;
        var rating = item.CommunityRating?.ToString("0.0", CultureInfo.CurrentCulture);
        return new MetadataLine(string.Join("  ·  ", parts), badge, rating);
    }

    public bool HasBadge => Badge is not null;
    public bool HasRating => Rating is not null;
}

/// <summary>A titled row of cards.</summary>
public sealed record Shelf(string Title, System.Collections.Generic.IReadOnlyList<ItemCard> Cards);
