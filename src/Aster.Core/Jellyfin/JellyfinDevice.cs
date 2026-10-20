namespace Aster.Core.Jellyfin;

/// <summary>
/// How this app introduces itself to the server, in every request's
/// Authorization header and on the server's Devices page.
/// </summary>
public static class JellyfinDevice
{
    public const string ClientName = "Aster";
    public const string Version = "1.0";

    public static string DeviceName => Environment.MachineName;

    private static readonly Lazy<string> deviceId = new(LoadOrCreateDeviceId);

    /// <summary>
    /// A random id made on first launch and kept, so the server sees one
    /// device across sign-ins.
    /// </summary>
    public static string DeviceId => deviceId.Value;

    private static string LoadOrCreateDeviceId()
    {
        var file = Path.Combine(AppPaths.Data, "device-id");
        try
        {
            var existing = File.ReadAllText(file).Trim();
            if (existing.Length > 0) return existing;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        var fresh = Guid.NewGuid().ToString("D").ToUpperInvariant();
        try { File.WriteAllText(file, fresh); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return fresh;
    }
}

/// <summary>Where the app keeps its files.</summary>
public static class AppPaths
{
    /// <summary>%LOCALAPPDATA%\Aster, created on first use.</summary>
    public static string Data { get; } = Create(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aster"));

    public static string ImageCache { get; } = Create(Path.Combine(Data, "ImageCache"));

    private static string Create(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
