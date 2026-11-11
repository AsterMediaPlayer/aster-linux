using System.Globalization;

namespace Aster.Core;

public enum SubtitleSize { Small, Normal, Large, ExtraLarge }

public enum SubtitleColor { White, Yellow }

public enum SubtitleBackground { Outline, Box }

/// <summary>
/// How plain-text subtitles (SRT, WebVTT, a server's converted ones) are
/// drawn, chosen in Settings ▸ Audio &amp; Subtitles, as the Android app
/// takes the system's caption style. Styled (ASS) subtitles keep their own
/// colours and outlines, but take the size too.
/// </summary>
public static class SubtitleStyle
{
    public static string Title(this SubtitleSize size) => size switch
    {
        SubtitleSize.Small => "Small",
        SubtitleSize.Large => "Large",
        SubtitleSize.ExtraLarge => "Extra Large",
        _ => "Normal",
    };

    public static string Title(this SubtitleColor color) => color == SubtitleColor.Yellow ? "Yellow" : "White";

    public static string Title(this SubtitleBackground background) => background == SubtitleBackground.Box ? "Dark Box" : "Outline";

    /// <summary>mpv's options for the style.</summary>
    public static IReadOnlyDictionary<string, string> MpvOptions(SubtitleSize size, SubtitleColor color, SubtitleBackground background)
    {
        var scale = size switch
        {
            SubtitleSize.Small => 0.8,
            SubtitleSize.Large => 1.25,
            SubtitleSize.ExtraLarge => 1.5,
            _ => 1.0,
        };
        var options = new Dictionary<string, string>
        {
            ["sub-scale"] = scale.ToString(CultureInfo.InvariantCulture),
            ["sub-color"] = color == SubtitleColor.Yellow ? "#FFFFE14D" : "#FFFFFFFF",
        };
        if (background == SubtitleBackground.Box)
        {
            // A box behind each line, as TV captions are, rather than an outline round the letters.
            options["sub-border-style"] = "background-box";
            options["sub-back-color"] = "#C0000000";
        }
        return options;
    }
}
