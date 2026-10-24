// Turns a menu's picture and sound references into addresses, accepting
// only what CLIENT_GUIDE §4.8 allows. A port of the Apple app's
// DiscPictureResolver (in DiscMenuController.swift).

using Aster.Core.Jellyfin;

namespace Aster.Core.DiscMenu;

public sealed class DiscPictureResolver(JellyfinClient client, string itemId)
{
    public Uri? Picture(string? reference) =>
        DiscMenuRules.IsSafeImage(reference) ? Address(reference!) : null;

    public Uri? Audio(string? reference) =>
        DiscMenuRules.IsSafeAudio(reference) ? Address(reference!) : null;

    private Uri? Address(string reference) =>
        reference.StartsWith("asset:", StringComparison.Ordinal)
            ? client.DiscAssetUrl(reference["asset:".Length..].Split('/'))
            : Uri.TryCreate(reference, UriKind.Absolute, out var url) ? url : null;

    /// <summary>The still picture of a background, if it has one.</summary>
    public Uri? Background(DiscBackground? background)
    {
        switch (background?.Source)
        {
            case "jellyfin":
                var kind = Enum.TryParse<ImageKind>(background.ImageType ?? "Backdrop", out var parsed) ? parsed : ImageKind.Backdrop;
                return client.ImageUrl(itemId, kind, index: Math.Max(0, background.Index ?? 0), maxWidth: 1920);
            case "tmdb":
                if (!DiscMenuRules.IsTmdbPath(background.TmdbFilePath)) return null;
                var size = background.TmdbSize is "w780" or "w1280" or "original" ? background.TmdbSize : "w1280";
                return new Uri($"https://image.tmdb.org/t/p/{size}{background.TmdbFilePath}");
            case "fanart":
                if (!DiscMenuRules.IsFanartId(background.FanartId) || !DiscMenuRules.IsItemId(itemId)) return null;
                return client.FanartUrl(itemId, background.FanartId!);
            case "image":
                return Picture(background.Image);
            default:
                return null;
        }
    }
}
