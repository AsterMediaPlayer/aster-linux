using System.Security.Cryptography;
using System.Text;

namespace Aster.Core.Jellyfin;

/// <summary>
/// Pictures and sounds kept on disk by address. Jellyfin's image addresses
/// carry the image's tag, which changes when the picture does, so a file
/// here never goes stale; it only ages out once the cache is over its size.
/// Stands in for the URLCache the Apple app gives its image session.
/// </summary>
public static class ImageCache
{
    public const long DefaultLimit = 512L * 1024 * 1024;

    public static string PathFor(Uri url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri));
        return Path.Combine(AppPaths.ImageCache, Convert.ToHexStringLower(hash));
    }

    public static async Task<byte[]?> ReadAsync(Uri url, CancellationToken ct)
    {
        var file = PathFor(url);
        try
        {
            var data = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
            // Reading counts as use, for trimming.
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            return data;
        }
        catch (FileNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static async Task WriteAsync(Uri url, byte[] data, CancellationToken ct)
    {
        var file = PathFor(url);
        var partial = file + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllBytesAsync(partial, data, ct).ConfigureAwait(false);
            File.Move(partial, file, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    public static void Remove(Uri url)
    {
        try { File.Delete(PathFor(url)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Deletes the least recently used files until the cache fits in <paramref name="limit"/> bytes.</summary>
    public static void Trim(long limit = DefaultLimit)
    {
        var files = new DirectoryInfo(AppPaths.ImageCache).EnumerateFiles()
            .OrderByDescending(f => f.LastAccessTimeUtc)
            .ToList();
        long total = 0;
        foreach (var file in files)
        {
            total += file.Length;
            if (total <= limit) continue;
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
