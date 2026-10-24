using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aster.Core.DiscMenu;
using Aster.Core.Jellyfin;

namespace Aster.Core.Tests;

/// <summary>
/// The JellyDisc conformance vectors (conformance/*.json from the jellydisc
/// repository) against the port of the menu rules, as the Apple app's
/// DiscMenuConformanceTests run them. The files are copies of the Apple
/// app's AsterFINTests/Conformance: a change there is copied here.
/// </summary>
public class DiscMenuConformanceTests
{
    private static string Folder([CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Conformance");

    private static JsonArray Cases(string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(Folder(), name)))!["cases"]!.AsArray();

    private static T Decode<T>(JsonNode node) => node.Deserialize<T>(JellyfinJson.Options)!;

    private static bool SameNumber(double? a, double? b) => a is null ? b is null : b is not null && Math.Abs(a.Value - b.Value) <= 1e-6;

    private static bool SamePosition(DiscPosition? a, DiscPosition? b) =>
        a is null ? b is null : b is not null
            && SameNumber(a.X, b.X) && SameNumber(a.Y, b.Y) && SameNumber(a.W, b.W) && SameNumber(a.H, b.H)
            && (a.Anchor ?? "top-left") == (b.Anchor ?? "top-left");

    /// <summary>Two JSON values alike, numbers within 1e-6 and a missing key the same as null.</summary>
    private static bool SameJson(JsonNode? a, JsonNode? b)
    {
        if (a is null || b is null) return a is null && b is null;
        switch (a)
        {
            case JsonObject objectA when b is JsonObject objectB:
                var keys = objectA.Where(p => p.Value is not null).Select(p => p.Key)
                    .Union(objectB.Where(p => p.Value is not null).Select(p => p.Key));
                return keys.All(k => SameJson(objectA[k], objectB[k]));
            case JsonArray arrayA when b is JsonArray arrayB:
                return arrayA.Count == arrayB.Count && arrayA.Zip(arrayB).All(p => SameJson(p.First, p.Second));
            case JsonValue valueA when b is JsonValue valueB:
                if (valueA.GetValueKind() == JsonValueKind.Number && valueB.GetValueKind() == JsonValueKind.Number)
                {
                    return SameNumber(valueA.GetValue<double>(), valueB.GetValue<double>());
                }
                return JsonNode.DeepEquals(valueA, valueB);
            default:
                return false;
        }
    }

    private static List<DiscMenuEntry> ContentEntries(int count, bool back, bool home)
    {
        var entries = Enumerable.Range(1, count).Select(n => new DiscMenuEntry { Action = "playExtra", Label = $"E{n}" }).ToList();
        if (back) entries.Add(new DiscMenuEntry { Action = "back", Label = "Back" });
        if (home) entries.Add(new DiscMenuEntry { Action = "home", Label = "Home" });
        return entries;
    }

    private static string[] Labels(JsonNode? node) => node?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];

    [Fact]
    public void Paginate()
    {
        var checkedCount = 0;
        foreach (var c in Cases("paginate.json"))
        {
            IReadOnlyList<DiscMenuEntry> entries = c!["entries"] is { } raw
                ? Decode<List<DiscMenuEntry>>(raw)
                : ContentEntries(c["n"]?.GetValue<int>() ?? 0, c["back"]?.GetValue<bool>() ?? false, c["home"]?.GetValue<bool>() ?? false);
            var pages = DiscMenuRules.Paginate(entries, c["slots"]!.GetValue<int>(), c["maxPerPage"]?.GetValue<int>() ?? 0);
            var expected = c["pages"]!.AsArray();
            Assert.True(pages.Count == expected.Count, $"page count for {c.ToJsonString()}");
            foreach (var (page, want) in pages.Zip(expected))
            {
                Assert.True(page.Items.Select(e => e.Label).SequenceEqual(Labels(want!["items"])), $"items for {c.ToJsonString()}");
                Assert.True(page.Backs.Select(e => e.Label).SequenceEqual(Labels(want["backs"])), $"backs for {c.ToJsonString()}");
                Assert.True(page.Prev == (want["prev"]?.GetValue<bool>() ?? false), $"prev for {c.ToJsonString()}");
                Assert.True(page.More == (want["more"]?.GetValue<bool>() ?? false), $"more for {c.ToJsonString()}");
            }
            checkedCount++;
        }
        Assert.Equal(318, checkedCount);
    }

    [Fact]
    public void CellPositions()
    {
        var checkedCount = 0;
        foreach (var c in Cases("cells.json"))
        {
            var flow = Decode<DiscFlow>(c!["flow"]!);
            var got = DiscMenuRules.CellPosition(flow, c["slot"]!.GetValue<int>(), c["tall"]?.GetValue<bool>() ?? false);
            Assert.True(SamePosition(got, Decode<DiscPosition>(c["position"]!)), $"cell {c.ToJsonString()}");
            checkedCount++;
        }
        Assert.Equal(163, checkedCount);
    }

    [Fact]
    public void Layouts()
    {
        var documents = new Dictionary<string, DiscMenuDocument>();
        var checkedCount = 0;
        foreach (var c in Cases("layout.json"))
        {
            var name = c!["document"]!.GetValue<string>();
            if (!documents.TryGetValue(name, out var document))
            {
                document = JsonSerializer.Deserialize<DiscMenuDocument>(File.ReadAllText(Path.Combine(Folder(), "documents", name)), JellyfinJson.Options)!;
                documents[name] = document;
            }
            var menuKey = c["menu"]!.GetValue<string>();
            var menu = document.Menus[menuKey];
            var page = c["page"]!.GetValue<int>();

            var merged = DiscMenuRules.EffectiveLayout(document.Layout, menu.Layout);
            Assert.True(SameJson(JsonSerializer.SerializeToNode(merged, JellyfinJson.Options), c["effectiveLayout"]), $"effective layout of {name}/{menuKey}");

            var result = DiscMenuRules.EntriesForPage(document, menu, page);
            Assert.True(result.PageCount == c["pages"]!.GetValue<int>(), $"page count of {name}/{menuKey}");
            Assert.True(result.Page == page, $"page of {name}/{menuKey}");

            var expected = c["entries"]!.AsArray();
            Assert.True(result.Entries.Count == expected.Count, $"entry count of {name}/{menuKey} page {page}");
            foreach (var (entry, want) in result.Entries.Zip(expected))
            {
                Assert.Equal(want!["Action"]!.GetValue<string>(), entry.Action);
                Assert.Equal(want["Label"]!.GetValue<string>(), entry.Label);
                var wantPosition = want["Position"] is { } raw ? Decode<DiscPosition>(raw) : null;
                Assert.True(SamePosition(entry.Position, wantPosition), $"position of {entry.Label} in {name}/{menuKey} page {page}");
            }
            checkedCount++;
        }
        Assert.Equal(27, checkedCount);
    }

    [Fact]
    public void Focus()
    {
        var directions = new[] { ("right", 1, 0), ("left", -1, 0), ("down", 0, 1), ("up", 0, -1) };
        var checkedCount = 0;
        foreach (var c in Cases("focus.json"))
        {
            var rects = c!["rects"]!.AsArray()
                .Select(r => new DiscRect(Number(r!["left"]), Number(r["top"]), Number(r["width"]), Number(r["height"])))
                .ToList();
            var from = c["from"]!.GetValue<int>();
            foreach (var (name, dx, dy) in directions)
            {
                var got = DiscMenuRules.MoveFocus(rects, from, dx, dy);
                Assert.True(got == c["expected"]![name]!.GetValue<int>(), $"{c["layout"]} from {from} {name}");
            }
            checkedCount++;
        }
        Assert.Equal(62, checkedCount);
    }

    private static double Number(JsonNode? node) => node?.GetValue<double>() ?? 0;

    [Fact]
    public void TimeFormat()
    {
        var checkedCount = 0;
        foreach (var c in Cases("time-format.json"))
        {
            Assert.Equal(c!["text"]!.GetValue<string>(), DiscMenuRules.FormatTicks(c["ticks"]!.GetValue<long>()));
            checkedCount++;
        }
        Assert.Equal(14, checkedCount);
    }

    [Fact]
    public void Safety()
    {
        var checkedCount = 0;
        foreach (var c in Cases("safety.json"))
        {
            var kind = c!["kind"]!.GetValue<string>();
            var value = c["value"]!.GetValue<string>();
            Func<string, bool> check = kind switch
            {
                "image" => DiscMenuRules.IsSafeImage,
                "audio" => DiscMenuRules.IsSafeAudio,
                "colour" => DiscMenuRules.IsHexColor,
                "tmdb" => DiscMenuRules.IsTmdbPath,
                "fanartId" => DiscMenuRules.IsFanartId,
                "itemId" => DiscMenuRules.IsItemId,
                _ => throw new InvalidDataException($"unknown safety kind {kind}"),
            };
            Assert.True(check(value) == c["accepted"]!.GetValue<bool>(), $"{kind} {JsonSerializer.Serialize(value)}");
            checkedCount++;
        }
        Assert.Equal(90, checkedCount);
    }
}
