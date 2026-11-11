using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core;

/// <summary>
/// This computer's settings, kept in %LOCALAPPDATA%\Aster\settings.json.
/// The names and defaults are the Apple app's.
/// </summary>
/// <summary>Back in a video: ask each time, to the mini player, or stop.</summary>
public enum VideoBack { Ask, MiniPlayer, Stop }

public sealed record AppSettings
{
    /// <summary>The streaming limit in bits per second; the top choice means "no limit".</summary>
    public int MaxBitrate { get; init; } = 120_000_000;

    /// <summary>Plays the next episode when the Up Next countdown ends.</summary>
    public bool AutoplayNext { get; init; } = true;

    /// <summary>Skips intros by themselves, once each.</summary>
    public bool AutoSkipIntro { get; init; }

    /// <summary>
    /// A video's Dolby and DTS sound goes to the receiver (or TV) as it is,
    /// over HDMI, for it to decode: Dolby Atmos (in E-AC-3 or TrueHD) and
    /// DTS:X included. Off, mpv decodes it to plain multichannel sound. This
    /// computer's own setting, as the receiver is: not synced.
    /// </summary>
    public bool AudioPassthrough { get; init; }

    /// <summary>
    /// The Sonos (its RINCON_ id) the volume last went to in passthrough: the
    /// one chosen again when two are on their TV inputs at once. Found by
    /// itself, not set by hand. This computer's own: not synced.
    /// </summary>
    public string? SonosSpeaker { get; init; }

    /// <summary>
    /// A video's sound brought to an even, game-like loudness as it plays
    /// (ffmpeg's loudnorm, about -16 LUFS), so TV mastered near -24 isn't
    /// far quieter than games and quiet scenes come up. Its channels stay as
    /// they are (5.1 stays 5.1). Not with passthrough, where the receiver
    /// decodes the sound. This computer's own setting: not synced.
    /// </summary>
    public bool EvenVolume { get; init; }

    /// <summary>
    /// A video's surround sound folded to stereo, the dialogue (the centre)
    /// at full level in both speakers and the surrounds at -3 dB: clearer and
    /// louder on a soundbar or TV without rear speakers, as games' stereo is.
    /// With <see cref="EvenVolume"/>, evened out after the fold, and louder
    /// (about -13 LUFS) than surround can be. Not with passthrough.
    /// </summary>
    public bool StereoMix { get; init; }

    /// <summary>ASS and SSA subtitles drawn as styled; off, as plain text at the bottom.</summary>
    public bool StyledSubtitles { get; init; } = true;

    /// <summary>Subtitles' size, colour and background (<see cref="SubtitleStyle"/>).</summary>
    public SubtitleSize SubtitleSize { get; init; } = SubtitleSize.Normal;

    public SubtitleColor SubtitleColor { get; init; }

    public SubtitleBackground SubtitleBackground { get; init; }

    /// <summary>The song card as an anime episode's opening or ending plays, and on music videos.</summary>
    public bool ThemeCards { get; init; } = true;

    /// <summary>Openings and endings picked on a show's Music tab play in the Now Playing panel, one after another; off, full screen, one at a time.</summary>
    public bool ThemePanel { get; init; } = true;

    /// <summary>The version of each song a show's Music tab plays, where the OP/ED plugin has it (else the nearest it has).</summary>
    public Jellyfin.ThemeCut ThemeCut { get; init; }

    /// <summary>A title's theme song, played quietly on its page.</summary>
    public bool ThemeMusic { get; init; }

    /// <summary>A title's theme video, looped silently behind its page.</summary>
    public bool ThemeVideos { get; init; }

    /// <summary>An anime opening looping behind a show's page (with no theme video of its own) plays its sound, quietly.</summary>
    public bool OpeningSound { get; init; }

    /// <summary>
    /// An opening's or ending's song named from AnimeThemes (animethemes.moe)
    /// where the server doesn't name it; off, AnimeThemes is asked nothing.
    /// </summary>
    public bool SongLookups { get; init; } = true;

    /// <summary>Play on a title with a JellyDisc menu opens its menu, like putting in the disc.</summary>
    public bool PreferDiscMenu { get; init; }

    /// <summary>A disc menu's music and button sounds.</summary>
    public bool DiscMenuSounds { get; init; } = true;

    /// <summary>A video takes the whole screen as it starts (the Mac's setting).</summary>
    public bool FullScreenPlayback { get; init; } = true;

    /// <summary>What Back does in a video played from the app's pages: ask, the mini player, or stop.</summary>
    public VideoBack BackInVideo { get; init; } = VideoBack.Ask;

    /// <summary>
    /// The synced settings (by their server keys) chosen on this computer, so
    /// one never chosen keeps the default rather than overwriting another
    /// device's choice on the server. See <see cref="SettingsSync"/>.
    /// </summary>
    public IReadOnlyList<string> Chosen { get; init; } = [];

    /// <summary>
    /// The saved servers (<see cref="SavedServer.Id"/>) whose welcome screen,
    /// what Aster and its companion plugins do, was seen on this computer.
    /// </summary>
    public IReadOnlyList<string> WelcomeSeen { get; init; } = [];

    /// <summary>Below the top choice, a file over the limit is converted down to it.</summary>
    public bool LimitsQuality => MaxBitrate < 120_000_000;

    private static string filePath = Path.Combine(AppPaths.Data, "settings.json");
    private static readonly Lock Gate = new();
    private static AppSettings? current;

    public static AppSettings Current
    {
        get
        {
            lock (Gate) return current ??= Load();
        }
    }

    /// <summary>Keeps the settings in another file, for tests, which mustn't touch the real ones.</summary>
    internal static void UseFile(string path)
    {
        lock (Gate)
        {
            filePath = path;
            current = null;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            current = settings;
            try { File.WriteAllText(filePath, JsonSerializer.Serialize(settings, JellyfinJson.Options)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), JellyfinJson.Options) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
}
