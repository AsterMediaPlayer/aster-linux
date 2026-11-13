// The TV home screen's Continue Watching row (Plasma Bigscreen), made by
// Aster itself: "Aster --home-screen-row" prints it as JSON and exits, with
// no window. It reads the server in use with Aster's own sign-in, so the home
// screen needs no key of its own, and prints [] whenever there's nothing to
// show (signed out, or that account turned it off).

using System.Text.Json;
using Aster.Core.Jellyfin;
using Aster.Core.Server;

namespace Aster.Core.Integration;

public static class HomeScreenRow
{
    public static async Task<string> JsonAsync(CancellationToken ct = default)
    {
        try
        {
            return JsonSerializer.Serialize(await RowsAsync(ct).ConfigureAwait(false));
        }
        catch (Exception error) when (error is JellyfinException or HttpRequestException or TaskCanceledException or IOException or JsonException)
        {
            return "[]";
        }
    }

    private static async Task<IReadOnlyList<Dictionary<string, object?>>> RowsAsync(CancellationToken ct)
    {
        if (StoredSession.Load() is not { } session) return [];
        var servers = SavedServers.Load();
        var saved = servers.FirstOrDefault(s => s.Id == SavedServer.IdOf(session));
        if (saved is { ShowsOnHomeScreen: false }) return [];

        var client = MediaServers.For(session);
        var items = await client.ResumeAsync(20, ct: ct).ConfigureAwait(false);
        // The server's name only when there's more than one to tell apart.
        var serverLabel = servers.Count > 1 ? saved?.Name ?? session.ServerName : null;

        var rows = new List<Dictionary<string, object?>>();
        foreach (var item in items)
        {
            var episode = item.Type == "Episode";
            var data = item.UserData;
            var progress = Math.Clamp((data?.PlayedPercentage ?? 0) / 100, 0, 1);
            var leftMinutes = Math.Max(0, ((item.RunTimeTicks ?? 0) - (data?.PlaybackPositionTicks ?? 0)) / 600_000_000);

            var backdrop =
                await ImageAsync(client, item.Id, ImageKind.Backdrop, item.BackdropImageTags?.FirstOrDefault(), 1920, ct)
                ?? await ImageAsync(client, item.ParentBackdropItemId, ImageKind.Backdrop, item.ParentBackdropImageTags?.FirstOrDefault(), 1920, ct)
                ?? await ImageAsync(client, item.Id, ImageKind.Thumb, Tag(item, "Thumb"), 1280, ct)
                ?? await ImageAsync(client, item.Id, ImageKind.Primary, Tag(item, "Primary"), 1280, ct);
            var logo =
                await ImageAsync(client, item.Id, ImageKind.Logo, Tag(item, "Logo"), 800, ct)
                ?? await ImageAsync(client, item.ParentLogoItemId, ImageKind.Logo, item.ParentLogoImageTag, 800, ct);

            string title, subtitle, line1;
            if (episode)
            {
                var code = item.ParentIndexNumber is { } s && item.IndexNumber is { } e ? $"S{s} E{e}" : null;
                title = item.SeriesName ?? item.Name ?? "";
                subtitle = Join(" · ", code, item.Name);
                line1 = Join(" · ", title, subtitle, item.OfficialRating);
            }
            else
            {
                title = item.Name ?? "";
                subtitle = item.ProductionYear?.ToString() ?? "";
                line1 = Join(" · ", subtitle, item.OfficialRating, string.Join(" / ", (item.Genres ?? []).Take(3)));
            }

            rows.Add(new()
            {
                ["itemId"] = item.Id,
                ["title"] = title,
                ["subtitle"] = subtitle,
                ["backdrop"] = backdrop ?? "",
                ["logo"] = logo ?? "",
                ["progress"] = Math.Round(progress, 4),
                ["line1"] = line1,
                ["overview"] = string.Join(' ', (item.Overview ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                ["line3"] = Join(" · ", $"{Math.Round(progress * 100)}% watched", leftMinutes > 0 ? $"{leftMinutes} min left" : null, serverLabel),
            });
        }
        return rows;
    }

    private static string? Tag(BaseItem item, string kind) =>
        item.ImageTags is { } tags && tags.TryGetValue(kind, out var tag) ? tag : null;

    /// <summary>The picture's file in Aster's image cache (fetched once), or null when the item has none.</summary>
    private static async Task<string?> ImageAsync(IMediaServer client, string? itemId, ImageKind kind, string? tag, int width, CancellationToken ct)
    {
        if (itemId is null || tag is null) return null;
        var url = client.ImageUrl(itemId, kind, tag, maxWidth: width);
        if (await ImageCache.ReadAsync(url, ct).ConfigureAwait(false) is null)
        {
            try
            {
                await ImageCache.WriteAsync(url, await client.DataAsync(url, ct: ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }
            catch (Exception error) when (error is JellyfinException or HttpRequestException)
            {
                return null;
            }
        }
        return ImageCache.PathFor(url);
    }

    private static string Join(string separator, params string?[] parts) =>
        string.Join(separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
