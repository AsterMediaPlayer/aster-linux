using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core;

/// <summary>
/// This computer's settings, kept in %LOCALAPPDATA%\Aster\settings.json.
/// The names and defaults are the Apple app's.
/// </summary>
public sealed record AppSettings
{
    /// <summary>The streaming limit in bits per second; the top choice means "no limit".</summary>
    public int MaxBitrate { get; init; } = 120_000_000;

    /// <summary>Plays the next episode when the Up Next countdown ends.</summary>
    public bool AutoplayNext { get; init; } = true;

    /// <summary>Skips intros by themselves, once each.</summary>
    public bool AutoSkipIntro { get; init; }

    /// <summary>Below the top choice, a file over the limit is converted down to it.</summary>
    public bool LimitsQuality => MaxBitrate < 120_000_000;

    private static readonly string FilePath = Path.Combine(AppPaths.Data, "settings.json");
    private static readonly Lock Gate = new();
    private static AppSettings? current;

    public static AppSettings Current
    {
        get
        {
            lock (Gate) return current ??= Load();
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Gate)
        {
            current = settings;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JellyfinJson.Options)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JellyfinJson.Options) ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
}
