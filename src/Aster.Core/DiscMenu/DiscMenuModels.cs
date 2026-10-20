// The resolved disc-menu document served by the JellyDisc plugin at
// GET /DiscMenus/{itemId}/Menu (see docs/CLIENT_GUIDE.md §3). Unknown
// properties are ignored; unknown enum values fall back to defaults at use.

using System.Text.Json.Serialization;

namespace Aster.Core.DiscMenu;

public sealed record DiscMenuDocument
{
    public string? MenuId { get; init; }
    public required string Root { get; init; }
    public IReadOnlyDictionary<string, DiscMenuScreenSpec> Menus { get; init; } = new Dictionary<string, DiscMenuScreenSpec>();
    public DiscBackground? Background { get; init; }
    public DiscTheme? Theme { get; init; }
    public DiscLayout? Layout { get; init; }
    public DiscAudio? Audio { get; init; }
    public IReadOnlyList<DiscChapter>? Chapters { get; init; }
    public IReadOnlyList<DiscTrailer>? Trailers { get; init; }
    public IReadOnlyList<string>? ThemeSongs { get; init; }
}

public sealed record DiscMenuScreenSpec
{
    public required string Title { get; init; }
    public IReadOnlyList<DiscMenuEntry> Entries { get; init; } = [];
    public DiscBackground? Background { get; init; }
    public DiscTheme? Theme { get; init; }
    public DiscLayout? Layout { get; init; }
    public DiscAudio? Audio { get; init; }
    public int? MaxPerPage { get; init; }
}

public enum DiscAction
{
    Unknown,
    PlayFeature,
    PlayExtra,
    PlaySequence,
    Submenu,
    Chapters,
    Back,
    Home,

    // Client-only actions: never sent by the server.
    PlayChapter,
    PagePrev,
    PageNext,
}

public sealed record DiscMenuEntry
{
    public required string Action { get; init; }
    public required string Label { get; init; }
    public DiscPosition? Position { get; init; }
    public string? Style { get; init; }
    public string? Image { get; init; }
    public string? ImageFocus { get; init; }
    public int? StartChapter { get; init; }
    public string? ItemId { get; init; }
    public IReadOnlyList<string>? ItemIds { get; init; }
    public string? Menu { get; init; }
    public int? PerPage { get; init; }

    // Produced by the client for chapter and paging buttons; never decoded.
    [JsonIgnore] public string? Sub { get; init; }
    [JsonIgnore] public Uri? ThumbUrl { get; init; }
    [JsonIgnore] public long? StartTicks { get; init; }

    [JsonIgnore]
    public DiscAction Kind =>
        Enum.TryParse<DiscAction>(Action, ignoreCase: true, out var kind) ? kind : DiscAction.Unknown;

    /// <summary>True when the cell should be given a height (artwork or a chapter thumbnail).</summary>
    [JsonIgnore]
    public bool IsTall => !string.IsNullOrEmpty(Image) || ThumbUrl is not null;
}

public sealed record DiscPosition(double X, double Y, double? W = null, double? H = null, string? Anchor = null);

public sealed record DiscBackground
{
    public string? Source { get; init; }
    public string? ImageType { get; init; }
    public int? Index { get; init; }
    public string? TmdbFilePath { get; init; }
    public string? TmdbSize { get; init; }
    public string? FanartId { get; init; }
    public string? Color { get; init; }
    public string? Image { get; init; }
    public int? TrailerIndex { get; init; }
    public bool? Muted { get; init; }
    public string? Poster { get; init; }
    public double? Dim { get; init; }
}

public sealed record DiscTheme
{
    public string? Id { get; init; }
    public string? Accent { get; init; }
    public string? Align { get; init; }
    public string? TextColor { get; init; }
    public string? Font { get; init; }
    public double? FontSize { get; init; }
    public bool? Uppercase { get; init; }
    public bool? Bold { get; init; }
    public double? LetterSpacing { get; init; }
}

public sealed record DiscLayout
{
    public DiscPosition? TitlePosition { get; init; }
    public bool? HideTitle { get; init; }
    public string? ButtonStyle { get; init; }
    public DiscFlow? Flow { get; init; }
    public DiscTransition? Transition { get; init; }
    public IReadOnlyList<DiscLayer>? Layers { get; init; }
}

public sealed record DiscFlow
{
    public required DiscPosition Region { get; init; }
    public int Columns { get; init; }
    public int Rows { get; init; }
    public string? MoreLabel { get; init; }
    public string? PreviousLabel { get; init; }
}

public sealed record DiscTransition
{
    public string? Style { get; init; }
    public int? DurationMs { get; init; }
}

public sealed record DiscLayer
{
    public string? Type { get; init; }
    public DiscPosition? Position { get; init; }
    public string? Fill { get; init; }
    public double? Opacity { get; init; }
    public string? BorderColor { get; init; }
    public double? BorderWidth { get; init; }
    public double? Radius { get; init; }
    public string? Image { get; init; }
    public string? Fit { get; init; }
}

public sealed record DiscAudio
{
    public DiscMusic? Music { get; init; }
    public DiscSounds? Sounds { get; init; }
}

public sealed record DiscMusic
{
    public string? Source { get; init; }
    public string? File { get; init; }
    public double? Volume { get; init; }
}

public sealed record DiscSounds
{
    public string? Preset { get; init; }
    public double? Volume { get; init; }
    public string? Move { get; init; }
    public string? Select { get; init; }
    public string? Back { get; init; }
}

public sealed record DiscChapter
{
    public int Index { get; init; }
    public string? Name { get; init; }
    public long StartTicks { get; init; }
    public bool? HasImage { get; init; }
    public long? ImageStamp { get; init; }
}

public sealed record DiscTrailer
{
    public string? Kind { get; init; }
    public string? ItemId { get; init; }
    public string? VideoId { get; init; }
    public string? Name { get; init; }
}
