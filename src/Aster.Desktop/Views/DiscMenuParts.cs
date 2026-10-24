// The pieces a disc menu is drawn from: its theme as fonts, colours and
// sizes (CLIENT_GUIDE §5.2), the buttons, backgrounds and layers. A port of
// the Apple app's DiscMenuStyle and the views of DiscMenuView.swift.
//
// Everything is laid out on a 1920 × 1080 canvas that the menu view scales
// to the window, so sizes are the Apple app's fractions of that.

using System;
using System.Collections.Generic;
using System.Globalization;
using Aster.Core.DiscMenu;
using Aster.Desktop.Controls;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Aster.Desktop.Views;

/// <summary>A screen's theme, as what draws it.</summary>
public sealed class DiscMenuStyle
{
    public const double Width = 1920;
    public const double Height = 1080;

    private static readonly Color DefaultAccent = Color.FromRgb(0x3D, 0xDC, 0x84);
    public static readonly Color Ground = Color.FromRgb(0x10, 0x10, 0x10);

    public DiscMenuStyle(DiscTheme theme)
    {
        Theme = theme;
        Accent = Hex(theme.Accent) ?? DefaultAccent;
        TextColor = Hex(theme.TextColor) ?? Colors.White;
        if (theme.FontSize is { } size && double.IsFinite(size) && size > 0)
        {
            var clamped = Math.Clamp(size, 1, 10);
            ButtonFont = Height * clamped / 100;
            TitleFont = Height * clamped * 1.6 / 100;
        }
        else
        {
            ButtonFont = BaseEm * 1.25;
            TitleFont = BaseEm * 2;
        }
    }

    public DiscTheme Theme { get; }
    public Color Accent { get; }
    public Color TextColor { get; }

    /// <summary>The base unit: the overlay's font size in the web reference.</summary>
    public double BaseEm => Height * 0.024;

    public double ButtonFont { get; }
    public double TitleFont { get; }

    /// <summary>1 vh: one percent of the canvas height.</summary>
    public static double Vh => Height / 100;

    /// <summary>Exactly <c>#rrggbb</c>, otherwise null.</summary>
    public static Color? Hex(string? hex) =>
        DiscMenuRules.IsHexColor(hex) ? Color.FromRgb(Convert.ToByte(hex![1..3], 16), Convert.ToByte(hex[3..5], 16), Convert.ToByte(hex[5..7], 16)) : null;

    public HorizontalAlignment Alignment => Theme.Align switch
    {
        "center" => HorizontalAlignment.Center,
        "right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left,
    };

    public TextAlignment TextAlignment => Theme.Align switch
    {
        "center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    /// <summary>Styled menu text: the theme's font, case and tracking, and the soft shadow.</summary>
    public TextBlock Text(string text, double size, Color? color = null, double opacity = 1)
    {
        var block = new TextBlock
        {
            Text = Theme.Uppercase == true ? text.ToUpper(CultureInfo.CurrentCulture) : text,
            FontSize = size,
            FontWeight = Theme.Bold == true ? FontWeight.Bold : FontWeight.Normal,
            Foreground = new SolidColorBrush(color ?? TextColor, opacity),
            TextAlignment = TextAlignment,
            TextWrapping = TextWrapping.Wrap,
            Effect = TextShadow,
        };
        if (Theme.LetterSpacing is { } spacing && double.IsFinite(spacing)) block.LetterSpacing = size * Math.Clamp(spacing, 0, 1);
        switch (Theme.Font)
        {
            case "serif":
                block.FontFamily = new FontFamily("Georgia, Noto Serif, DejaVu Serif, Times New Roman, serif");
                break;
            case "mono":
                block.FontFamily = new FontFamily("Cascadia Mono, Consolas, Noto Sans Mono, DejaVu Sans Mono, monospace");
                break;
            case "condensed":
                block.FontStretch = FontStretch.Condensed;
                break;
            case "wide":
                block.FontStretch = FontStretch.Expanded;
                break;
        }
        return block;
    }

    public static DropShadowEffect TextShadow => new() { OffsetX = 0, OffsetY = 2, BlurRadius = 6, Color = Colors.Black, Opacity = 0.8 };

    public DropShadowEffect Glow(double radius) => new() { OffsetX = 0, OffsetY = 0, BlurRadius = radius, Color = Accent, Opacity = 1 };
}

/// <summary>
/// One button of a menu screen, drawn for its kind: a chapter's thumbnail,
/// a picture (the menu's own artwork), or text in one of the four styles
/// (frame, the default; text; glow; arrow).
/// </summary>
public sealed class DiscButtonView : Border
{
    private readonly Action<bool> showFocus;

    public DiscButtonView(DiscMenuEntry entry, DiscMenuStyle style, string? defaultStyle, DiscPictureResolver resolver)
    {
        Entry = entry;
        Avalonia.Automation.AutomationProperties.SetName(this, entry.Label);
        var sized = entry.Position?.W is not null || entry.Position?.H is not null;
        if (entry.ThumbUrl is { } thumb) showFocus = ChapterButton(entry, thumb, style);
        else if (resolver.Picture(entry.Image) is { } image) showFocus = ArtworkButton(image, resolver.Picture(entry.ImageFocus), style, sized);
        else showFocus = TextButton(entry, style, defaultStyle, sized);
        showFocus(false);
    }

    public DiscMenuEntry Entry { get; }

    public void SetFocused(bool focused) => showFocus(focused);

    private static readonly Transitions Quick =
    [
        new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new Avalonia.Animation.Easings.QuadraticEaseOut() },
    ];

    private Action<bool> TextButton(DiscMenuEntry entry, DiscMenuStyle style, string? defaultStyle, bool sized)
    {
        var name = entry.Style ?? defaultStyle ?? "frame";
        if (name is not ("text" or "frame" or "glow" or "arrow")) name = "frame";
        var size = style.ButtonFont;

        var label = style.Text(entry.Label, size);
        var lines = new StackPanel { HorizontalAlignment = style.Alignment };
        lines.Children.Add(label);
        if (entry.Sub is { } sub) lines.Children.Add(style.Text(sub, size * 0.65, opacity: 0.75));

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = sized ? style.Alignment : HorizontalAlignment.Left };
        TextBlock? arrow = null;
        if (name == "arrow")
        {
            arrow = new TextBlock
            {
                Text = "▶",
                FontSize = size,
                Foreground = new SolidColorBrush(style.Accent),
                Margin = new Thickness(0, 0, size * 0.5, 0),
                VerticalAlignment = VerticalAlignment.Top,
                Transitions = Quick,
            };
            row.Children.Add(arrow);
        }
        row.Children.Add(lines);

        var frame = new Border
        {
            Child = row,
            Padding = new Thickness(size * 1.25, size * 0.5),
            CornerRadius = new CornerRadius(size * 0.4),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = sized ? HorizontalAlignment.Stretch : style.Alignment,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Child = frame;
        HorizontalAlignment = sized ? HorizontalAlignment.Stretch : style.Alignment;
        VerticalAlignment = VerticalAlignment.Center;

        return focused =>
        {
            frame.Background = name == "frame" ? new SolidColorBrush(Colors.Black, focused ? 0.75 : 0.5) : Brushes.Transparent;
            frame.BorderBrush = name == "frame" && focused ? new SolidColorBrush(style.Accent) : Brushes.Transparent;
            if (name == "text") label.Foreground = new SolidColorBrush(focused ? style.Accent : style.TextColor);
            if (arrow is not null) arrow.Opacity = focused ? 1 : 0;
            Effect = name == "glow" && focused ? style.Glow(22) : null;
        };
    }

    /// <summary>The picture is the button; the label is only its accessible name.</summary>
    private Action<bool> ArtworkButton(Uri image, Uri? focusImage, DiscMenuStyle style, bool sized)
    {
        var picture = new RemoteImage { Url = image, Stretch = Stretch.Uniform };
        if (!sized)
        {
            picture.MaxWidth = DiscMenuStyle.Width * 0.4;
            picture.MaxHeight = DiscMenuStyle.Height * 0.25;
        }
        Child = picture;
        Transitions = Quick;
        return focused =>
        {
            picture.Url = focused && focusImage is not null ? focusImage : image;
            // No focus picture of its own: it brightens and glows.
            var plain = focused && focusImage is null;
            Opacity = focusImage is not null || focused ? 1 : 0.85;
            Effect = plain ? style.Glow(10) : null;
        };
    }

    /// <summary>Scene selection: a 16:9 thumbnail with "Label · time" below.</summary>
    private Action<bool> ChapterButton(DiscMenuEntry entry, Uri thumb, DiscMenuStyle style)
    {
        var captionSize = DiscMenuStyle.Vh * 1.6;
        var padding = captionSize * 0.3;
        var caption = style.Text(entry.Label + (entry.Sub is { } sub ? $" · {sub}" : ""), captionSize);
        caption.TextAlignment = TextAlignment.Left;
        caption.MaxLines = 2;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var content = new StackPanel { Spacing = padding, VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(new SixteenByNineBox
        {
            Child = new Border
            {
                Background = Brushes.Black,
                CornerRadius = new CornerRadius(captionSize * 0.3),
                ClipToBounds = true,
                Child = new RemoteImage { Url = thumb, Stretch = Stretch.UniformToFill },
            },
        });
        content.Children.Add(caption);
        Child = content;
        Padding = new Thickness(padding);
        CornerRadius = new CornerRadius(captionSize * 0.4);
        BorderThickness = new Thickness(2);
        ClipToBounds = true;
        return focused =>
        {
            Background = new SolidColorBrush(Colors.Black, focused ? 0.65 : 0.35);
            BorderBrush = focused ? new SolidColorBrush(style.Accent) : Brushes.Transparent;
        };
    }
}

/// <summary>A screen's background and decorative layers.</summary>
public static class DiscMenuBackdrops
{
    /// <summary>
    /// The still part of a background: a colour, a picture, or a trailer's
    /// poster (over the trailer until it plays), dimmed. <paramref name="poster"/>
    /// is the poster, to fade (by its opacity) once the trailer's playing.
    /// </summary>
    public static Control Still(DiscBackground? background, DiscPictureResolver resolver, out Control? poster)
    {
        poster = null;
        var panel = new Panel { IsHitTestVisible = false };
        switch (background?.Source)
        {
            case "color":
                panel.Background = new SolidColorBrush(DiscMenuStyle.Hex(background.Color) ?? DiscMenuStyle.Ground);
                break;
            case "trailer":
                if (resolver.Picture(background.Poster) is { } posterUrl)
                {
                    // Wrapped: the picture fades in by its own opacity as it loads.
                    poster = new Border
                    {
                        Child = new RemoteImage { Url = posterUrl, Stretch = Stretch.UniformToFill },
                        Transitions = [new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromSeconds(0.8), Easing = new Avalonia.Animation.Easings.QuadraticEaseIn() }],
                    };
                    panel.Children.Add(poster);
                }
                panel.Children.Add(Dim(background.Dim, 0.25));
                break;
            case "jellyfin" or "tmdb" or "fanart" or "image":
                if (resolver.Background(background) is { } url) panel.Children.Add(new RemoteImage { Url = url, Stretch = Stretch.UniformToFill });
                panel.Children.Add(Dim(background.Dim, 0.4));
                break;
        }
        return panel;
    }

    private static Border Dim(double? dim, double fallback) =>
        new() { Background = new SolidColorBrush(Colors.Black, DiscMenuRules.Clamp(dim, 0, 1, fallback)) };

    /// <summary>Panels and pictures drawn over the background, under the buttons.</summary>
    public static Control Layers(IReadOnlyList<DiscLayer> layers, DiscPictureResolver resolver)
    {
        var canvas = new DiscCanvasPanel { IsHitTestVisible = false };
        foreach (var layer in layers)
        {
            if (layer.Position is not { } position) continue;
            Control? drawn = layer.Type switch
            {
                "panel" => Panel(layer),
                "image" when resolver.Picture(layer.Image) is { } url => new RemoteImage
                {
                    Url = url,
                    Stretch = layer.Fit switch { "cover" => Stretch.UniformToFill, "fill" => Stretch.Fill, _ => Stretch.Uniform },
                    ClipToBounds = layer.Fit == "cover",
                },
                _ => null,
            };
            if (drawn is null) continue;
            // An image fades in as it loads; the layer's own opacity sits round it.
            var wrapper = new Border { Child = drawn, Opacity = DiscMenuRules.Clamp(layer.Opacity, 0, 1, 1) };
            DiscCanvasPanel.SetPosition(wrapper, position);
            canvas.Children.Add(wrapper);
        }
        return canvas;
    }

    private static Border Panel(DiscLayer layer)
    {
        var radius = DiscMenuRules.Clamp(layer.Radius, 0, 100, 0) * DiscMenuStyle.Vh;
        var borderWidth = DiscMenuRules.Clamp(layer.BorderWidth, 0, 100, 0) * DiscMenuStyle.Vh;
        return new Border
        {
            Background = DiscMenuStyle.Hex(layer.Fill) is { } fill ? new SolidColorBrush(fill) : Brushes.Transparent,
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(borderWidth),
            BorderBrush = borderWidth > 0 && DiscMenuStyle.Hex(layer.BorderColor) is { } border ? new SolidColorBrush(border) : Brushes.Transparent,
        };
    }
}
