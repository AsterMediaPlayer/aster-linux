using Avalonia.Data.Converters;

namespace Aster.Desktop.Controls;

public static class Converters
{
    /// <summary>A page dot: bright for the page showing, faint for the rest.</summary>
    public static readonly IValueConverter DotOpacity =
        new FuncValueConverter<bool, double>(current => current ? 0.95 : 0.35);
}
