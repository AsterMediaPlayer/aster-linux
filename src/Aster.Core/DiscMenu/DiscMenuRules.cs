// The screen-independent rules of the JellyDisc format (CLIENT_GUIDE §4):
// paging, grid cells, settings inheritance, directional focus, chapter time
// text and what to accept from untrusted data. Every function here is pure
// and is checked against the plugin's conformance vectors by the tests. A
// port of the Apple app's DiscMenuRules.swift; keep the two in step.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Aster.Core.DiscMenu;

/// <summary>A button's frame on screen, in whatever units the caller measures in.</summary>
public readonly record struct DiscRect(double X, double Y, double Width, double Height)
{
    public double MinX => X;
    public double MinY => Y;
    public double MaxX => X + Width;
    public double MaxY => Y + Height;
    public double MidX => X + Width / 2;
    public double MidY => Y + Height / 2;

    public bool Contains(double x, double y) => x >= MinX && x < MaxX && y >= MinY && y < MaxY;
}

public static class DiscMenuRules
{
    // ---- Anchors and placement ----

    /// <summary>Shift of the element, as a fraction of its own size, for each anchor name.</summary>
    public static (double X, double Y) AnchorShift(string? anchor) => (anchor ?? "top-left") switch
    {
        "top" => (-0.5, 0),
        "top-right" => (-1, 0),
        "left" => (0, -0.5),
        "center" => (-0.5, -0.5),
        "right" => (-1, -0.5),
        "bottom-left" => (0, -1),
        "bottom" => (-0.5, -1),
        "bottom-right" => (-1, -1),
        _ => (0, 0),
    };

    // ---- Paging ----

    public sealed record Page(IReadOnlyList<DiscMenuEntry> Items, IReadOnlyList<DiscMenuEntry> Backs, bool Prev, bool More);

    /// <summary>
    /// Splits a screen's entries over the pages of a <paramref name="slots"/>-cell
    /// grid. The first <c>back</c> and the first <c>home</c> entry are pinned to every page.
    /// </summary>
    public static IReadOnlyList<Page> Paginate(IReadOnlyList<DiscMenuEntry> entries, int slots, int maxPerPage = 0)
    {
        var maxPer = maxPerPage > 0 ? maxPerPage : int.MaxValue;
        var firstBack = IndexOf(entries, "back");
        var firstHome = IndexOf(entries, "home");
        var backs = new List<DiscMenuEntry>();
        var content = new List<DiscMenuEntry>();
        for (var index = 0; index < entries.Count; index++)
        {
            if (index == firstBack || index == firstHome) backs.Add(entries[index]);
            else content.Add(entries[index]);
        }
        var pages = new List<Page>();
        var taken = 0;
        while (true)
        {
            var prev = pages.Count > 0;
            var room = slots - (backs.Count + (prev ? 1 : 0));
            var left = content.Count - taken;
            if (left <= Math.Min(room, maxPer))
            {
                pages.Add(new Page(content.GetRange(taken, left), backs, prev, More: false));
                return pages;
            }
            var cap = Math.Min(Math.Max(1, room - 1), maxPer);
            pages.Add(new Page(content.GetRange(taken, cap), backs, prev, More: true));
            taken += cap;
        }
    }

    private static int IndexOf(IReadOnlyList<DiscMenuEntry> entries, string action)
    {
        for (var index = 0; index < entries.Count; index++) if (entries[index].Action == action) return index;
        return -1;
    }

    /// <summary>The on-screen position of grid cell <paramref name="slot"/> (row-major from 0) of a flow.</summary>
    public static DiscPosition CellPosition(DiscFlow flow, int slot, bool tall)
    {
        var region = flow.Region;
        var shift = AnchorShift(region.Anchor);
        var width = region.W ?? 0;
        var height = region.H ?? 0;
        var left = region.X + shift.X * width;
        var top = region.Y + shift.Y * height;
        var columns = Math.Max(1, flow.Columns);
        var rows = Math.Max(1, flow.Rows);
        var cellW = width / columns;
        var cellH = height / rows;
        return new DiscPosition(
            X: left + (slot % columns + 0.5) * cellW,
            Y: top + (slot / columns + 0.5) * cellH,
            W: cellW * 0.94,
            H: tall ? cellH * 0.94 : null,
            Anchor: "center");
    }

    // ---- Inheritance ----

    /// <summary>Document layout, then the screen's, key by key. A missing or null key is skipped.</summary>
    public static DiscLayout EffectiveLayout(DiscLayout? document, DiscLayout? screen) => new()
    {
        TitlePosition = screen?.TitlePosition ?? document?.TitlePosition,
        HideTitle = screen?.HideTitle ?? document?.HideTitle,
        ButtonStyle = screen?.ButtonStyle ?? document?.ButtonStyle,
        Flow = screen?.Flow ?? document?.Flow,
        Transition = screen?.Transition ?? document?.Transition,
        Layers = screen?.Layers ?? document?.Layers,
    };

    /// <summary>Theme and background are whole-object: the screen's, else the document's.</summary>
    public static DiscTheme EffectiveTheme(DiscTheme? document, DiscTheme? screen) => screen ?? document ?? new DiscTheme();

    public static DiscBackground? EffectiveBackground(DiscBackground? document, DiscBackground? screen) => screen ?? document;

    /// <summary>Audio merges per part: Music and Sounds each fall back separately.</summary>
    public static DiscAudio? EffectiveAudio(DiscAudio? document, DiscAudio? screen) =>
        document is null && screen is null ? null : new DiscAudio { Music = screen?.Music ?? document?.Music, Sounds = screen?.Sounds ?? document?.Sounds };

    // ---- Entries for a page ----

    public sealed record PageLayout(IReadOnlyList<DiscMenuEntry> Entries, int PageCount, int Page, bool Positioned);

    /// <summary>
    /// The buttons to draw on <paramref name="requested"/> page of a screen, in
    /// drawing order, with their final positions (null = the default column).
    /// </summary>
    public static PageLayout EntriesForPage(DiscMenuDocument document, DiscMenuScreenSpec screen, int requested)
    {
        var layout = EffectiveLayout(document.Layout, screen.Layout);
        if (layout.Flow is not { } flow)
        {
            var positioned = screen.Entries.Count > 0 && screen.Entries.All(e => e.Position is not null);
            return new PageLayout(screen.Entries, 1, 0, positioned);
        }
        var slots = Math.Max(1, flow.Columns) * Math.Max(1, flow.Rows);
        var pages = Paginate(screen.Entries, slots, screen.MaxPerPage ?? 0);
        var page = Math.Max(0, Math.Min(requested, pages.Count - 1));
        var current = pages[page];
        var nav = current.Backs.ToList();
        if (current.Prev) nav.Add(new DiscMenuEntry { Action = "pagePrev", Label = flow.PreviousLabel ?? "Previous" });
        if (current.More) nav.Add(new DiscMenuEntry { Action = "pageNext", Label = flow.MoreLabel ?? "More" });
        var placed = new List<DiscMenuEntry>();
        for (var index = 0; index < current.Items.Count; index++)
        {
            var entry = current.Items[index];
            placed.Add(entry with { Position = CellPosition(flow, index, entry.IsTall) });
        }
        for (var index = 0; index < nav.Count; index++)
        {
            var entry = nav[index];
            placed.Add(entry with { Position = CellPosition(flow, slots - nav.Count + index, !string.IsNullOrEmpty(entry.Image)) });
        }
        return new PageLayout(placed, pages.Count, page, Positioned: true);
    }

    // ---- Scene selection ----

    /// <summary>The grid used for a scene-selection screen when neither the styling screen nor the document declares a <c>Flow</c>.</summary>
    public static DiscFlow DefaultChapterFlow(int perPage)
    {
        var slots = perPage + 3;
        var columns = perPage <= 3 ? Math.Max(1, perPage) : perPage <= 8 ? 3 : 4;
        var rows = (int)Math.Ceiling((double)slots / columns);
        return new DiscFlow { Region = new DiscPosition(50, 52, 86, 68, "center"), Columns = columns, Rows = rows };
    }

    // ---- Focus ----

    /// <summary>
    /// Index of the button that takes focus when (<paramref name="dx"/>,
    /// <paramref name="dy"/>) is pressed on <paramref name="current"/>, or
    /// <paramref name="current"/> when nothing moves. Rectangles are the
    /// buttons' on-screen frames in drawing order.
    /// </summary>
    public static int MoveFocus(IReadOnlyList<DiscRect> rects, int current, int dx, int dy)
    {
        if (current < 0 || current >= rects.Count) return current;
        var from = rects[current];
        int? best = null;
        (int Tier, double Distance)? bestKey = null;
        int? wrap = null;
        (double Far, double Across)? wrapKey = null;

        for (var index = 0; index < rects.Count; index++)
        {
            if (index == current) continue;
            var rect = rects[index];
            var across = AcrossDistance(from, rect, dx);
            if (DistanceAlong(from, rect, dx, dy) is { } ahead)
            {
                var key = OverlapsAcross(from, rect, dx) ? (0, ahead) : (1, ahead + 2 * across);
                if (bestKey is not { } currentBest || key.Item1 < currentBest.Tier || (key.Item1 == currentBest.Tier && key.Item2 < currentBest.Distance))
                {
                    best = index;
                    bestKey = key;
                }
                continue;
            }
            if (DistanceAlong(from, rect, -dx, -dy) is not { } behind) continue;
            var far = -behind;
            if (wrapKey is not { } currentWrap || far < currentWrap.Far - 20 || (far < currentWrap.Far + 20 && across < currentWrap.Across))
            {
                wrap = index;
                wrapKey = (far, across);
            }
        }
        return best ?? wrap ?? current;
    }

    private static double? DistanceAlong(DiscRect from, DiscRect rect, int dx, int dy)
    {
        var tolerance = 0.25 * (dx != 0 ? from.Width : from.Height);
        var gap = dx == 1 ? rect.MinX - from.MaxX
            : dx == -1 ? from.MinX - rect.MaxX
            : dy == 1 ? rect.MinY - from.MaxY
            : from.MinY - rect.MaxY;
        if (gap < -tolerance) return null;
        var cx = rect.MidX - from.MidX;
        var cy = rect.MidY - from.MidY;
        return cx * dx + cy * dy;
    }

    private static bool OverlapsAcross(DiscRect from, DiscRect rect, int dx) =>
        dx != 0
            ? from.MinY < rect.MaxY && rect.MinY < from.MaxY
            : from.MinX < rect.MaxX && rect.MinX < from.MaxX;

    private static double AcrossDistance(DiscRect from, DiscRect rect, int dx) =>
        Math.Abs(dx != 0 ? rect.MidY - from.MidY : rect.MidX - from.MidX);

    // ---- Time ----

    /// <summary>Chapter start time: h:mm:ss from one hour, otherwise m:ss (minutes not padded).</summary>
    public static string FormatTicks(long ticks)
    {
        var total = Math.Max(0, ticks) / 10_000_000;
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    // ---- Untrusted data ----

    private const string Segment = "[A-Za-z0-9][A-Za-z0-9._-]{0,63}";
    private const string Https = """https://[^\s"'()<>\\]+""";
    private const string AssetReference = $"asset:{Segment}(?:/{Segment}){{0,3}}";

    // \z rather than $, which would also match before a final newline.
    private static readonly Regex SafeImage = Whole($"(?:{Https}|data:image/(?:png|jpeg|webp);base64,[A-Za-z0-9+/=]+|{AssetReference})");
    private static readonly Regex SafeAudio = Whole($"(?:{Https}|asset:{Segment}(?:/{Segment}){{0,3}}\\.(?:mp3|ogg|opus|m4a|wav))");
    private static readonly Regex HexColor = Whole("#[0-9a-fA-F]{6}");
    private static readonly Regex TmdbPath = Whole("/[A-Za-z0-9_-]+\\.(?:jpg|png)");
    private static readonly Regex FanartId = Whole("[0-9]{1,12}");
    private static readonly Regex ItemId = Whole("[0-9a-fA-F-]{32,36}");

    private static Regex Whole(string pattern) => new($"^{pattern}\\z", RegexOptions.CultureInvariant);

    public static bool IsSafeImage(string? value) => value is not null && SafeImage.IsMatch(value);
    public static bool IsSafeAudio(string? value) => value is not null && SafeAudio.IsMatch(value);
    public static bool IsHexColor(string? value) => value is not null && HexColor.IsMatch(value);
    public static bool IsTmdbPath(string? value) => value is not null && TmdbPath.IsMatch(value);
    public static bool IsFanartId(string? value) => value is not null && FanartId.IsMatch(value);
    public static bool IsItemId(string? value) => value is not null && ItemId.IsMatch(value);

    /// <summary>Clamps a number into a range, treating non-finite values as the default.</summary>
    public static double Clamp(double? value, double min, double max, double fallback) =>
        value is { } v && double.IsFinite(v) ? Math.Min(max, Math.Max(min, v)) : fallback;
}
