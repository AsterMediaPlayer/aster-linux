using Aster.Desktop.ViewModels;
using Avalonia;
using Avalonia.Controls.Templates;
using Avalonia.Data.Converters;

namespace Aster.Desktop.Controls;

public static class Converters
{
    /// <summary>A page dot: bright for the page showing, faint for the rest.</summary>
    public static readonly IValueConverter DotOpacity =
        new FuncValueConverter<bool, double>(current => current ? 0.95 : 0.35);

    /// <summary>Bold for the current one (a lyric line being sung), regular for the rest.</summary>
    public static readonly IValueConverter Bold =
        new FuncValueConverter<bool, Avalonia.Media.FontWeight>(current => current ? Avalonia.Media.FontWeight.SemiBold : Avalonia.Media.FontWeight.Normal);

    /// <summary>The template for a kind of card: <c>PosterCard</c>, <c>SquareCard</c> and so on, from Templates.axaml.</summary>
    public static readonly IValueConverter CardTemplate =
        new FuncValueConverter<CardStyle, IDataTemplate?>(style =>
            Application.Current?.TryGetResource($"{style}Card", null, out var template) == true ? template as IDataTemplate : null);
}
