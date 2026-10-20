using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aster.Core.Jellyfin;

namespace Aster.Core.Playback;

/// <summary>One subtitle line, in seconds from the start of the title.</summary>
public sealed partial record SubtitleCue(double Start, double End, string Text)
{
    /// <summary>
    /// The cues showing at <paramref name="time"/>, top to bottom, or null for
    /// none. <paramref name="cues"/> must be sorted by start.
    /// </summary>
    public static string? TextAt(double time, IReadOnlyList<SubtitleCue> cues)
    {
        if (cues.Count == 0) return null;
        // First cue starting after `time`; everything before it may be showing.
        int low = 0, high = cues.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (cues[mid].Start <= time) low = mid + 1; else high = mid;
        }
        // Cues overlap rarely and briefly, so a short look back finds them all.
        var showing = new List<string>();
        for (var position = low - 1; position >= Math.Max(0, low - 8); position--)
        {
            if (cues[position].End > time) showing.Insert(0, cues[position].Text);
        }
        return showing.Count == 0 ? null : string.Join("\n", showing);
    }

    // ---- Parsing ----

    private sealed record Track(IReadOnlyList<TrackEvent>? TrackEvents);

    private sealed record TrackEvent(string? Text, long StartPositionTicks, long EndPositionTicks);

    /// <summary>
    /// Decodes Jellyfin's <c>Stream.js</c> response: <c>{"TrackEvents":[...]}</c>
    /// with positions in ticks and text that may carry HTML or ASS markup.
    /// </summary>
    public static IReadOnlyList<SubtitleCue> Parse(byte[] json)
    {
        Track? track;
        try { track = JsonSerializer.Deserialize<Track>(json, JellyfinJson.Options); }
        catch (JsonException error) { throw JellyfinException.Decoding(error.Message, error); }
        return (track?.TrackEvents ?? [])
            .Select(e => new SubtitleCue(e.StartPositionTicks / 10_000_000.0, e.EndPositionTicks / 10_000_000.0, PlainText(e.Text ?? "")))
            .Where(cue => cue.Text.Length > 0)
            .OrderBy(cue => cue.Start)
            .ToList();
    }

    private static readonly (string Entity, string Character)[] Entities =
    [
        ("&lt;", "<"), ("&gt;", ">"), ("&quot;", "\""), ("&apos;", "'"), ("&#39;", "'"), ("&nbsp;", "\u00A0"),
        // Last, so "&amp;lt;" reads as "&lt;" and not "<".
        ("&amp;", "&"),
    ];

    /// <summary>
    /// Cue text as it should read on screen: line breaks kept, markup
    /// (<c>&lt;i&gt;</c>, <c>{\an8}</c>, <c>&lt;br&gt;</c>) removed, entities decoded.
    /// </summary>
    public static string PlainText(string raw)
    {
        var text = WithoutDrawings(raw)
            .Replace("\r\n", "\n")
            .Replace("\\N", "\n")
            .Replace("\\n", "\n");
        text = LineBreakTag().Replace(text, "\n");
        text = HtmlTag().Replace(text, "");
        text = AssBlock().Replace(text, "");
        foreach (var (entity, character) in Entities) text = text.Replace(entity, character);
        return string.Join("\n", text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    /// <summary>
    /// ASS text with its vector drawings left out. <c>{\p1}</c> (any <c>\p</c>
    /// above 0) makes the text that follows shape commands, such as the boxes
    /// behind signs ("m 0 0 l 100 0 100 50"), until <c>{\p0}</c>; shown as
    /// text they read as coordinates. <c>\pos</c> and <c>\pbo</c> are other tags.
    /// </summary>
    public static string WithoutDrawings(string raw)
    {
        if (!raw.Contains("\\p", StringComparison.Ordinal)) return raw;
        var result = new StringBuilder();
        var drawing = false;
        var rest = raw.AsSpan();
        while (rest.IndexOf('{') is var open and >= 0)
        {
            if (!drawing) result.Append(rest[..open]);
            var close = rest[open..].IndexOf('}');
            if (close < 0)
            {
                rest = rest[open..];
                break;
            }
            var block = rest.Slice(open, close + 1).ToString();
            // The last \p in a block decides, as in a renderer.
            var matches = DrawingTag().Matches(block);
            if (matches.Count > 0) drawing = int.Parse(matches[^1].Groups[1].ValueSpan) > 0;
            result.Append(block);
            rest = rest[(open + close + 1)..];
        }
        if (!drawing) result.Append(rest);
        return result.ToString();
    }

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex AssBlock();

    [GeneratedRegex(@"\\p(\d+)(?![a-z])")]
    private static partial Regex DrawingTag();
}
