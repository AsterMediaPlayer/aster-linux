using System.Globalization;

namespace Aster.Core.Playback;

/// <summary>
/// Language codes as Jellyfin reports them (mostly ISO 639-2, three
/// letters) normalized to two-letter tags, so a track's language can be
/// matched against the user's preference whatever form each is in.
/// </summary>
public static class LanguageTag
{
    public static string Bcp47(string? code)
    {
        var text = code?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(text)) return "und";
        if (text.Length == 2) return text;
        if (Iso639_2To1.TryGetValue(text, out var mapped)) return mapped;
        try
        {
            var language = CultureInfo.GetCultureInfo(text).TwoLetterISOLanguageName;
            if (language.Length > 0 && language != "iv") return language;
        }
        catch (CultureNotFoundException) { }
        return text;
    }

    /// <summary>Whether two codes name the same language, whatever their form.</summary>
    public static bool SameLanguage(string? a, string? b)
    {
        if (a is null || b is null) return false;
        var left = Bcp47(a);
        return left != "und" && left == Bcp47(b);
    }

    private static readonly Dictionary<string, string> Iso639_2To1 = new()
    {
        ["aar"] = "aa", ["abk"] = "ab", ["afr"] = "af", ["aka"] = "ak", ["alb"] = "sq", ["sqi"] = "sq",
        ["amh"] = "am", ["ara"] = "ar", ["arg"] = "an", ["arm"] = "hy", ["hye"] = "hy", ["asm"] = "as",
        ["ava"] = "av", ["ave"] = "ae", ["aym"] = "ay", ["aze"] = "az", ["bak"] = "ba", ["bam"] = "bm",
        ["baq"] = "eu", ["eus"] = "eu", ["bel"] = "be", ["ben"] = "bn", ["bih"] = "bh", ["bis"] = "bi",
        ["bos"] = "bs", ["bre"] = "br", ["bul"] = "bg", ["bur"] = "my", ["mya"] = "my", ["cat"] = "ca",
        ["cha"] = "ch", ["che"] = "ce", ["chi"] = "zh", ["zho"] = "zh", ["chv"] = "cv", ["cor"] = "kw",
        ["cos"] = "co", ["cre"] = "cr", ["cze"] = "cs", ["ces"] = "cs", ["dan"] = "da", ["div"] = "dv",
        ["dut"] = "nl", ["nld"] = "nl", ["dzo"] = "dz", ["eng"] = "en", ["epo"] = "eo", ["est"] = "et",
        ["ewe"] = "ee", ["fao"] = "fo", ["fij"] = "fj", ["fin"] = "fi", ["fre"] = "fr", ["fra"] = "fr",
        ["fry"] = "fy", ["ful"] = "ff", ["geo"] = "ka", ["kat"] = "ka", ["ger"] = "de", ["deu"] = "de",
        ["gla"] = "gd", ["gle"] = "ga", ["glg"] = "gl", ["glv"] = "gv", ["gre"] = "el", ["ell"] = "el",
        ["grn"] = "gn", ["guj"] = "gu", ["hat"] = "ht", ["hau"] = "ha", ["heb"] = "he", ["her"] = "hz",
        ["hin"] = "hi", ["hmo"] = "ho", ["hrv"] = "hr", ["hun"] = "hu", ["ibo"] = "ig", ["ice"] = "is",
        ["isl"] = "is", ["ido"] = "io", ["iii"] = "ii", ["iku"] = "iu", ["ile"] = "ie", ["ina"] = "ia",
        ["ind"] = "id", ["ipk"] = "ik", ["ita"] = "it", ["jav"] = "jv", ["jpn"] = "ja", ["kal"] = "kl",
        ["kan"] = "kn", ["kas"] = "ks", ["kaz"] = "kk", ["khm"] = "km", ["kik"] = "ki", ["kin"] = "rw",
        ["kir"] = "ky", ["kom"] = "kv", ["kon"] = "kg", ["kor"] = "ko", ["kua"] = "kj", ["kur"] = "ku",
        ["lao"] = "lo", ["lat"] = "la", ["lav"] = "lv", ["lim"] = "li", ["lin"] = "ln", ["lit"] = "lt",
        ["ltz"] = "lb", ["lub"] = "lu", ["lug"] = "lg", ["mac"] = "mk", ["mkd"] = "mk", ["mah"] = "mh",
        ["mal"] = "ml", ["mao"] = "mi", ["mri"] = "mi", ["mar"] = "mr", ["may"] = "ms", ["msa"] = "ms",
        ["mlg"] = "mg", ["mlt"] = "mt", ["mon"] = "mn", ["nau"] = "na", ["nav"] = "nv", ["nbl"] = "nr",
        ["nde"] = "nd", ["ndo"] = "ng", ["nep"] = "ne", ["nno"] = "nn", ["nob"] = "nb", ["nor"] = "no",
        ["nya"] = "ny", ["oci"] = "oc", ["oji"] = "oj", ["ori"] = "or", ["orm"] = "om", ["oss"] = "os",
        ["pan"] = "pa", ["per"] = "fa", ["fas"] = "fa", ["pli"] = "pi", ["pol"] = "pl", ["por"] = "pt",
        ["pus"] = "ps", ["que"] = "qu", ["roh"] = "rm", ["rum"] = "ro", ["ron"] = "ro", ["run"] = "rn",
        ["rus"] = "ru", ["sag"] = "sg", ["san"] = "sa", ["sin"] = "si", ["slo"] = "sk", ["slk"] = "sk",
        ["slv"] = "sl", ["sme"] = "se", ["smo"] = "sm", ["sna"] = "sn", ["snd"] = "sd", ["som"] = "so",
        ["sot"] = "st", ["spa"] = "es", ["srd"] = "sc", ["srp"] = "sr", ["ssw"] = "ss", ["sun"] = "su",
        ["swa"] = "sw", ["swe"] = "sv", ["tah"] = "ty", ["tam"] = "ta", ["tat"] = "tt", ["tel"] = "te",
        ["tgk"] = "tg", ["tgl"] = "tl", ["tha"] = "th", ["tib"] = "bo", ["bod"] = "bo", ["tir"] = "ti",
        ["ton"] = "to", ["tsn"] = "tn", ["tso"] = "ts", ["tuk"] = "tk", ["tur"] = "tr", ["twi"] = "tw",
        ["uig"] = "ug", ["ukr"] = "uk", ["urd"] = "ur", ["uzb"] = "uz", ["ven"] = "ve", ["vie"] = "vi",
        ["vol"] = "vo", ["wel"] = "cy", ["cym"] = "cy", ["wln"] = "wa", ["wol"] = "wo", ["xho"] = "xh",
        ["yid"] = "yi", ["yor"] = "yo", ["zha"] = "za", ["zul"] = "zu",
    };
}
