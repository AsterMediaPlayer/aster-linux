using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core;

/// <summary>
/// The last of each screen's answers, kept on disk (the Android and Apple
/// apps' ResponseCache): shown the moment the screen opens, then replaced
/// by what the server says now. Each is a file named for its server, user
/// and screen, so one person's or server's never shows for another.
/// Nothing expires; a screen always asks again after showing it. Cleared
/// on sign-out.
/// </summary>
public static class Snapshots
{
    private static string folder = Path.Combine(AppPaths.Data, "Snapshots");

    /// <summary>Keeps them in another folder, for tests.</summary>
    internal static void UseFolder(string path) => folder = path;

    public static string Key(Uri server, string? userId, string screen) => $"{server.ToString().TrimEnd('/')}|{userId}|{screen}";

    private static string File(string key) =>
        Path.Combine(folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".json");

    /// <summary>What was kept under <paramref name="key"/>, or null when nothing was, or it can't be read any more.</summary>
    public static async Task<T?> ReadAsync<T>(string key) where T : class
    {
        try
        {
            await using var stream = new FileStream(File(key), FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            return await JsonSerializer.DeserializeAsync<T>(stream, JellyfinJson.Options).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) { return null; }
    }

    /// <summary>Keeps <paramref name="value"/>, whole or not at all.</summary>
    public static async Task WriteAsync<T>(string key, T value)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var target = File(key);
            var partial = target + ".part";
            await using (var stream = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                await JsonSerializer.SerializeAsync(stream, value, JellyfinJson.Options).ConfigureAwait(false);
            System.IO.File.Move(partial, target, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }

    /// <summary>Every screen kept, for every server: on sign-out.</summary>
    public static void Clear()
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
