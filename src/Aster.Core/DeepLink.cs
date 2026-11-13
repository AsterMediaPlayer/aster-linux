namespace Aster.Core;

public enum DeepLinkKind
{
    /// <summary><c>aster://item/{id}</c>: a title's page.</summary>
    Item,

    /// <summary><c>aster://play/{id}</c>: the title, playing from where it was left.</summary>
    Play,

    /// <summary><c>aster://channel/{id}</c>: a Channels-tab channel, tuned in.</summary>
    Channel,
}

/// <summary>
/// A link into the app, as the Apple app's DeepLinkRouter has them:
/// <c>aster://item/{id}</c>, <c>aster://play/{id}</c> and
/// <c>aster://channel/{id}</c> (<c>asterfin://</c>, the app's earlier name,
/// too). Opened from a browser, a launcher or another app.
/// </summary>
public sealed record DeepLink(DeepLinkKind Kind, string Id)
{
    /// <summary>The link in <paramref name="text"/>; null when it isn't one, or names nothing.</summary>
    public static DeepLink? Parse(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals("aster", StringComparison.OrdinalIgnoreCase) && !uri.Scheme.Equals("asterfin", StringComparison.OrdinalIgnoreCase))
            return null;
        DeepLinkKind? kind = uri.Host.ToLowerInvariant() switch
        {
            "item" => DeepLinkKind.Item,
            "play" => DeepLinkKind.Play,
            "channel" => DeepLinkKind.Channel,
            _ => null,
        };
        var id = Uri.UnescapeDataString(uri.AbsolutePath.Trim('/'));
        // One id, not a path: nothing else goes to the server.
        return kind is { } found && id.Length > 0 && !id.Contains('/') ? new DeepLink(found, id) : null;
    }

    /// <summary>The first link among a launch's arguments.</summary>
    public static DeepLink? Find(IEnumerable<string> args) => args.Select(Parse).FirstOrDefault(link => link is not null);

    public override string ToString() => $"aster://{Kind.ToString().ToLowerInvariant()}/{Uri.EscapeDataString(Id)}";
}
