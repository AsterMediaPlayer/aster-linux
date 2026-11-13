// What a TV home screen (Plasma Bigscreen's Continue Watching row) needs from
// Aster: which server and user are in use, and whether that account lets it
// show what's being watched. No token: the home screen reads the server with
// its own key.

using System.Text.Json;
using Aster.Core.Jellyfin;

namespace Aster.Core.Integration;

public static class HomeScreenExport
{
    /// <summary>~/.local/share/Aster/home-screen.json</summary>
    public static string FilePath => Path.Combine(AppPaths.Data, "home-screen.json");

    /// <summary>
    /// The server in use (null when signed out, or a development session):
    /// written whole and swapped in, so the home screen never reads half a file.
    /// </summary>
    public static void Write(SavedServer? server)
    {
        if (!OperatingSystem.IsLinux()) return;
        var session = server?.Session;
        var shown = server is { ShowsOnHomeScreen: true } && session!.Kind is ServerKind.Jellyfin or ServerKind.Emby;
        var data = new Dictionary<string, object?>
        {
            ["enabled"] = shown,
            ["serverUrl"] = shown ? session!.ServerUrl.AbsoluteUri : null,
            ["serverName"] = shown ? session!.ServerName : null,
            ["kind"] = shown ? session!.Kind.ToString() : null,
            ["userId"] = shown ? session!.UserId : null,
            ["userName"] = shown ? session!.UserName : null,
            ["updated"] = DateTimeOffset.UtcNow,
        };
        try
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
