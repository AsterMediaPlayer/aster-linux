// A library sorted by name, in letter sections with an index to jump
// between them. A port of the Apple app's LibraryIndex.swift.
//
// Titles sort as Jellyfin's sort name has them (no leading "The", "A" or
// "An"), compared the way the computer's language does, numbers in number
// order. The sections follow the writing system: accented letters go with
// their letter, Cyrillic or Greek get their own, Japanese kana go by row
// (あ か さ …), Korean by its first consonant, and numbers and symbols
// under #. Chinese characters go under # too: the Apple app files them by
// pinyin, which .NET has no table for.

using System.Globalization;
using System.Text;
using Aster.Core.Jellyfin;

namespace Aster.Core.Browsing;

/// <summary>How titles are put in order.</summary>
public static class TitleSort
{
    /// <summary>Leading words a title is sorted without, as Jellyfin does by default.</summary>
    private static readonly string[] Articles = ["the ", "a ", "an "];

    /// <summary>Case, accents and width as the language orders them, and "Episode 2" before "Episode 10".</summary>
    public static readonly StringComparer Comparer =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreWidth | CompareOptions.NumericOrdering);

    /// <summary>What a title sorts by: Jellyfin's sort name when it has one, else the title without a leading article.</summary>
    public static string Key(string title, string? sortName = null)
    {
        if (!string.IsNullOrEmpty(sortName)) return sortName;
        var trimmed = title.Trim();
        foreach (var article in Articles)
        {
            if (trimmed.Length > article.Length && trimmed.StartsWith(article, StringComparison.OrdinalIgnoreCase)) return trimmed[article.Length..];
        }
        return trimmed;
    }

    public static string Key(BaseItem item) => Key(item.DisplayTitle, item.SortName);
}

/// <summary>One letter's titles.</summary>
public sealed record IndexSection(string Key, IReadOnlyList<BaseItem> Items);

public static class LibraryIndex
{
    /// <summary>Where numbers, symbols and anything without a letter go: last.</summary>
    public const string Other = "#";

    /// <summary><paramref name="items"/> in order and in their letter sections.</summary>
    public static IReadOnlyList<IndexSection> Sections(IEnumerable<BaseItem> items)
    {
        var sorted = items.Select(item => (Item: item, Key: TitleSort.Key(item))).OrderBy(e => e.Key, TitleSort.Comparer);
        var order = new List<string>();
        var grouped = new Dictionary<string, List<BaseItem>>();
        foreach (var (item, key) in sorted)
        {
            var section = SectionKey(key);
            if (!grouped.TryGetValue(section, out var list))
            {
                grouped[section] = list = [];
                order.Add(section);
            }
            list.Add(item);
        }
        // Latin letters first, then other writing systems as the language orders them, # last.
        return order
            .OrderBy(k => k == Other ? 2 : IsLatinLetter(k) ? 0 : 1)
            .ThenBy(k => k, TitleSort.Comparer)
            .Select(k => new IndexSection(k, grouped[k]))
            .ToList();
    }

    /// <summary>The section a sort key goes in.</summary>
    public static string SectionKey(string text)
    {
        var first = text.EnumerateRunes().FirstOrDefault(r => !Rune.IsWhiteSpace(r) && !Rune.IsPunctuation(r) && !Rune.IsSymbol(r));
        if (first.Value == 0 || Rune.IsDigit(first)) return Other;
        var value = first.Value;

        // Korean: the syllable's first consonant, doubled ones with their single.
        if (value is >= 0xAC00 and <= 0xD7A3)
        {
            const string initials = "ㄱㄱㄴㄷㄷㄹㅁㅂㅂㅅㅅㅇㅈㅈㅊㅋㅌㅍㅎ";
            return initials[(value - 0xAC00) / 588].ToString();
        }
        // Japanese kana: its row, katakana with hiragana, voiced with plain.
        if (value is (>= 0x3041 and <= 0x3096) or (>= 0x30A1 and <= 0x30FA))
        {
            var hiragana = value >= 0x30A1 ? value - 0x60 : value;
            var plain = new Rune(hiragana).ToString().Normalize(NormalizationForm.FormD)[0].ToString();
            return KanaRows.FirstOrDefault(row => row.Contains(plain, StringComparison.Ordinal))?[0].ToString() ?? Other;
        }
        // Chinese, which the Apple app files by pinyin.
        if (value is (>= 0x4E00 and <= 0x9FFF) or (>= 0x3400 and <= 0x4DBF)) return Other;

        var letter = Folded(first.ToString());
        return letter.Length > 0 && char.IsLetter(letter, 0) ? char.ConvertFromUtf32(char.ConvertToUtf32(letter, 0)) : Other;
    }

    /// <summary>The gojūon rows, each led by its index letter.</summary>
    private static readonly string[] KanaRows =
        ["あいうえおぁぃぅぇぉ", "かきくけこゕゖ", "さしすせそ", "たちつてとっ", "なにぬねの", "はひふへほ", "まみむめも", "やゆよゃゅょ", "らりるれろ", "わゐゑをんゎ"];

    /// <summary>Without accents, width or case: "é" → "E", "ｆ" → "F".</summary>
    private static string Folded(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var kept = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) kept.Append(c);
        }
        return kept.ToString().ToUpper(CultureInfo.CurrentCulture);
    }

    private static bool IsLatinLetter(string key) => key.All(c => c is >= 'A' and <= 'Z');
}

/// <summary>The ways a library's titles can be ordered.</summary>
public enum LibrarySort
{
    Name,
    DateAdded,
    ReleaseDate,
    Rating,
}

public static class LibrarySortExtensions
{
    extension(LibrarySort sort)
    {
        public string Title => sort switch
        {
            LibrarySort.DateAdded => "Recently Added",
            LibrarySort.ReleaseDate => "Release Date",
            LibrarySort.Rating => "Rating",
            _ => "Name",
        };

        public (string SortBy, string Order) Query => sort switch
        {
            LibrarySort.DateAdded => ("DateCreated,SortName", "Descending"),
            LibrarySort.ReleaseDate => ("PremiereDate,SortName", "Descending"),
            LibrarySort.Rating => ("CommunityRating,SortName", "Descending"),
            _ => ("SortName", "Ascending"),
        };
    }
}
