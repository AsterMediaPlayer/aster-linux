using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Aster.Core.Jellyfin;
using Aster.Core.Ratings;
using Avalonia.Media;

namespace Aster.Desktop.ViewModels;

/// <summary>
/// "2019 · PG-13 · 2 hr 9 min · Action, Adventure", with its quality badge
/// and the sites' scores apart so they can be drawn as such.
/// </summary>
public sealed record MetadataLine(string Text, string? Badge, IReadOnlyList<ScoreBadge> Scores)
{
    /// <param name="showsUnwatched">A show's page: how many episodes are left to watch, after the years.</param>
    /// <param name="addedScores">MDBList's, over the server's own (Settings ▸ Title Pages ▸ Ratings).</param>
    public static MetadataLine For(BaseItem item, bool showsUnwatched = false, IReadOnlyList<TitleScore>? addedScores = null)
    {
        var parts = item.MetadataLine.ToList();
        if (showsUnwatched && item.Kind == ItemKind.Series && item.UserData?.UnplayedItemCount is > 0 and var unplayed)
        {
            parts.Insert(item.YearText is null ? 0 : 1, $"{unplayed} unwatched");
        }
        var badge = item.Is4K ? "4K" : item.IsHD ? "HD" : null;
        var scores = TitleScore.Merged(TitleScore.FromServer(item), addedScores ?? []);
        return new MetadataLine(string.Join("  ·  ", parts), badge, scores.Select(s => new ScoreBadge(s)).ToList());
    }

    public bool HasBadge => Badge is not null;
}

/// <summary>
/// A site's score as the details line shows it: the site's mark, then the
/// score as it writes it. As the Apple app's TitleScoreBadge, the marks
/// drawn rather than emoji: Rotten Tomatoes red when fresh, green when not.
/// </summary>
public sealed record ScoreBadge(TitleScore Score)
{
    public string Text => Score.Text;
    public string Tip => $"{Score.SiteName} {Score.Text}";

    public bool IsImdb => Score.Source == ScoreSource.Imdb;
    public bool IsTmdb => Score.Source == ScoreSource.Tmdb;
    public bool IsLetterboxd => Score.Source == ScoreSource.Letterboxd;
    public bool IsCommunity => Score.Source == ScoreSource.Community;

    /// <summary>A coloured tag with a letter or two: Rotten Tomatoes' (and its audience score's), and Metacritic's.</summary>
    public bool IsTag => Score.Source is ScoreSource.Tomatometer or ScoreSource.Popcorn or ScoreSource.Metacritic;

    public string TagText => Score.Source switch
    {
        ScoreSource.Metacritic => "M",
        ScoreSource.Popcorn => "RT Audience",
        _ => "RT",
    };

    public IBrush TagBackground => Score.Source == ScoreSource.Metacritic
        // Metacritic's own colours: green from 61, yellow from 40, else red.
        ? new SolidColorBrush(Score.Value * 10 >= 61 ? Color.FromRgb(0x66, 0xCC, 0x33) : Score.Value * 10 >= 40 ? Color.FromRgb(0xFF, 0xCC, 0x33) : Color.FromRgb(0xFF, 0x00, 0x00))
        : new SolidColorBrush(Score.IsFresh ? Color.FromRgb(0xFA, 0x32, 0x0A) : Color.FromRgb(0x0A, 0xC8, 0x55), Score.Source == ScoreSource.Popcorn ? 0.75 : 1);

    public IBrush TagForeground => Score.Source == ScoreSource.Metacritic ? Brushes.Black : Brushes.White;
}

/// <summary>A titled row of cards.</summary>
public sealed record Shelf(string Title, System.Collections.Generic.IReadOnlyList<ItemCard> Cards);
