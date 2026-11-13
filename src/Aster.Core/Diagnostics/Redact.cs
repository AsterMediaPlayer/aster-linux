using System.Text.RegularExpressions;

namespace Aster.Core.Diagnostics;

/// <summary>
/// What a debug report leaves out: anything that signs in (tokens, keys,
/// passwords), where the server is (addresses, host names, IPs), who's using
/// it (the user's name, email addresses, this computer's home folder), and
/// the library's file paths, which name the user's titles. Every line of a
/// report passes through <see cref="Text"/> before it leaves the computer.
/// The Android and Apple apps' Redact, rule for rule.
/// </summary>
public static class Redact
{
    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        // Tokens and keys in addresses and headers: api_key=…, ApiKey=…, X-Emby-Token: …, X-Plex-Token=…, Token="…".
        (new Regex("""(?i)\b(api_?key|access_?token|x-emby-token|x-mediabrowser-token|x-plex-token|token|password|pw)(["']?\s*[:=]\s*["']?)[^\s"'&,;}]+"""), "$1$2<hidden>"),
        // A MediaBrowser authorization header's token (and its device id, which can be traced to the computer).
        (new Regex("(?i)\\b(Token|DeviceId)=\"[^\"]*\""), "$1=\"<hidden>\""),
        // A bearer or basic authorization.
        (new Regex("""(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]{8,}"""), "$1 <hidden>"),
        // An address: scheme and host (with any user and port), the path kept, since it says what was asked for.
        (new Regex("""(?i)\b(https?|wss?|rtsp)://[^\s/"'<>]+"""), "$1://<server>"),
        // Plex's own host names carry the server's IP and id.
        (new Regex("""(?i)\b[0-9a-z-]+\.(?:[0-9a-f]+\.)?plex\.direct\b"""), "<server>"),
        // IPv4 and IPv6 addresses outside an address; a version number ("Linux 6.8.0.1", "v1.2.3.4") isn't one.
        (new Regex("""(?i)(?<!\b(?:os|version|build|v|linux|kernel)\s?)(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])"""), "<ip>"),
        (new Regex("""(?i)\b(?:[0-9a-f]{1,4}:){4,7}[0-9a-f]{1,4}\b"""), "<ip>"),
        // Email addresses.
        (new Regex("""[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}"""), "<email>"),
        // A media file's place in the library, which names the title: kept to its kind of file.
        (new Regex("""(?i)(?:[A-Z]:\\|/(?:mnt|media|data|srv|share|volume\d*|storage|home|Users|nas|library|movies|tv)/)[^\n"'<>]*?\.(mkv|mp4|m4v|avi|ts|m2ts|iso|mov|webm|mp3|flac|m4a|m4b|aac|ogg|opus|srt|ass|ssa|vtt|sup)\b"""), "<path>.$1"),
    ];

    /// <summary>A server's name that says nothing of whose it is (Emby and Jellyfin call themselves "Server"): left as it is.</summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase) { "server", "emby", "jellyfin", "plex", "media", "home", "localhost" };

    /// <summary>
    /// <paramref name="value"/> with everything above taken out; <paramref name="names"/>
    /// (the user's name and account id, the server's own name) go too, as
    /// whole words and never inside a placeholder already put in. An id goes
    /// with and without its dashes. <paramref name="home"/>, this computer's
    /// home folder, which names its user, becomes <c>~</c> first.
    /// </summary>
    public static string Text(string value, IEnumerable<string>? names = null, string? home = null)
    {
        var output = value;
        if (!string.IsNullOrEmpty(home) && home.Length > 1) output = output.Replace(home.TrimEnd('/', '\\'), "~", StringComparison.OrdinalIgnoreCase);
        foreach (var (pattern, replacement) in Rules) output = pattern.Replace(output, replacement);
        var words = (names ?? []).SelectMany(n => new[] { n, n.Replace("-", "", StringComparison.Ordinal) }).Distinct()
            .Where(w => w.Length >= 3 && !Generic.Contains(w)).OrderByDescending(w => w.Length);
        foreach (var name in words)
            output = Regex.Replace(output, "(?i)(?<![A-Za-z0-9<])" + Regex.Escape(name) + "(?![A-Za-z0-9>])", "<name>");
        return output;
    }
}
