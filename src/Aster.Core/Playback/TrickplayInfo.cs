namespace Aster.Core.Playback;

/// <summary>One resolution of a title's trickplay images, as Jellyfin lists them.</summary>
public sealed record TrickplayInfo
{
    /// <summary>Each frame's size in pixels.</summary>
    public int Width { get; init; }
    public int Height { get; init; }

    /// <summary>Frames across and down one sheet.</summary>
    public int TileWidth { get; init; }
    public int TileHeight { get; init; }

    public int ThumbnailCount { get; init; }

    /// <summary>Milliseconds between frames.</summary>
    public int Interval { get; init; }

    /// <summary>Where the frame for a moment is: which sheet, and its cell there.</summary>
    public readonly record struct Cell(int Sheet, int Column, int Row);

    public readonly record struct PixelRect(int X, int Y, int Width, int Height);

    public Cell? CellAt(double seconds)
    {
        if (Interval <= 0 || TileWidth <= 0 || TileHeight <= 0 || ThumbnailCount <= 0 || !double.IsFinite(seconds)) return null;
        var frame = Math.Clamp((int)(seconds * 1000) / Interval, 0, ThumbnailCount - 1);
        var perSheet = TileWidth * TileHeight;
        var inSheet = frame % perSheet;
        return new Cell(frame / perSheet, inSheet % TileWidth, inSheet / TileWidth);
    }

    /// <summary>The cell's rectangle in its sheet, in pixels.</summary>
    public PixelRect RectOf(Cell cell) => new(cell.Column * Width, cell.Row * Height, Width, Height);

    /// <summary>The resolution closest to <paramref name="width"/> among a source's, for a preview that size.</summary>
    public static TrickplayInfo? Best(IReadOnlyDictionary<string, TrickplayInfo> resolutions, int width) =>
        resolutions.Values.MinBy(info => Math.Abs(info.Width - width));
}
