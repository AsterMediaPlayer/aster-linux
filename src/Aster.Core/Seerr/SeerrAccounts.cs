using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aster.Core.Jellyfin;

namespace Aster.Core.Seerr;

/// <summary>
/// The Seerr sign-in, as every Aster app keeps it: in the accounts the
/// Apple and Android apps sync through the media server (TrackerVault, in
/// Aster's display preferences under "trackers"), and on this computer too
/// (the Secret Service; Windows' Credential Manager), so it works when the
/// server can't be reached. The newer copy wins. The other accounts in it
/// (AniList, Trakt and the rest, which this app doesn't use yet) are kept
/// as they came.
/// </summary>
public sealed class SeerrAccounts
{
    private JellyfinClient? client;
    private string? owner;
    private JsonObject secrets = [];

    /// <summary>The account, or null: not signed in, or Seerr ended the session (<see cref="NeedsSignIn"/>).</summary>
    public SeerrAccount? Account => !Allowed || NeedsSignIn || Stored is not { } stored ? null : reached is { } url ? stored with { Url = url } : stored;

    /// <summary>
    /// Whether the server lets Aster's Seerr features show: the Aster Tracking plugin's switch, so a server turns
    /// them on. Without the plugin they stay off.
    /// </summary>
    public bool Allowed { get; private set; } = true;

    /// <summary>Seerr's address as the server's plugin gives it, to start Settings › Requests with.</summary>
    public Uri? Suggested { get; private set; }

    /// <summary>Takes the server's Aster Tracking switch (null: no plugin, so off).</summary>
    public void UsePlugin(Aster.Core.Tracking.TrackingFeatures? features)
    {
        var allowed = features?.Seerr.Enabled == true;
        Suggested = features?.Seerr.Url is { } url ? SeerrClient.Address(url) : null;
        if (allowed == Allowed) return;
        Allowed = allowed;
        Changed?.Invoke();
    }

    /// <summary>
    /// Where this computer reaches Seerr, when the synced address won't do:
    /// a one-word name ("server:5055") that a phone finds on the local
    /// network, which Linux knows by mDNS as "server.local". The synced
    /// address is left as it is, for the other devices.
    /// </summary>
    private Uri? reached;

    /// <summary>The account as kept, even one whose session Seerr ended.</summary>
    public SeerrAccount? Stored { get; private set; }

    /// <summary>Seerr said the session's over: sign in again (Settings, or Discover).</summary>
    public bool NeedsSignIn { get; private set; }

    /// <summary>The account came, went or changed.</summary>
    public event Action? Changed;

    /// <summary>
    /// The accounts of <paramref name="jellyfin"/>'s user: this computer's
    /// copy, then the server's when it's newer (or this one's sent up when it is).
    /// </summary>
    public async Task LoadAsync(JellyfinClient jellyfin, CancellationToken ct = default)
    {
        if (jellyfin.UserId is not { } userId) return;
        client = jellyfin;
        var who = $"{jellyfin.BaseUrl.AbsoluteUri}|{userId}";
        if (who != owner)
        {
            owner = who;
            secrets = Local(who) ?? [];
            NeedsSignIn = false;
            Show();
        }
        JsonObject? server = null;
        try
        {
            var settings = await jellyfin.AppSettingsAsync(ct).ConfigureAwait(false);
            server = TrackerVault.Open(settings.GetValueOrDefault("trackers"), userId);
        }
        catch (Exception error) when (error is HttpRequestException or JellyfinException or TaskCanceledException or JsonException)
        {
            return;
        }
        if (server is null)
        {
            if (secrets.Count > 0) await WriteServerAsync().ConfigureAwait(false);
            return;
        }
        if (Updated(server) > Updated(secrets))
        {
            secrets = server;
            KeepLocal();
            Show();
        }
        else if (Updated(secrets) > Updated(server)) await WriteServerAsync().ConfigureAwait(false);
    }

    public void SignedIn(SeerrAccount account)
    {
        NeedsSignIn = false;
        Change(s => s["seerr"] = new JsonObject
        {
            ["url"] = account.Url.AbsoluteUri.TrimEnd('/'),
            ["cookie"] = account.Cookie,
            ["name"] = account.Name,
            ["userId"] = account.UserId,
        });
    }

    public void SignOut()
    {
        NeedsSignIn = false;
        Change(s => s.Remove("seerr"));
    }

    /// <summary>Seerr ended the session: it's asked for again.</summary>
    public void SessionEnded()
    {
        if (NeedsSignIn) return;
        NeedsSignIn = true;
        Changed?.Invoke();
    }

    private void Change(Action<JsonObject> edit)
    {
        edit(secrets);
        secrets["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        KeepLocal();
        Show();
        _ = WriteServerAsync();
    }

    private void Show()
    {
        Stored = Read(secrets["seerr"] as JsonObject);
        reached = null;
        Changed?.Invoke();
        if (Stored is { } stored) _ = ReachAsync(stored);
    }

    private async Task ReachAsync(SeerrAccount account)
    {
        var url = await SeerrClient.ReachableAsync(account.Url).ConfigureAwait(false);
        if (url == account.Url || Stored != account) return;
        reached = url;
        Changed?.Invoke();
    }

    private static SeerrAccount? Read(JsonObject? seerr)
    {
        try
        {
            if (seerr?["url"]?.GetValue<string>() is not { } url || seerr["cookie"]?.GetValue<string>() is not { } cookie) return null;
            return new SeerrAccount(new Uri(url), cookie, seerr["name"]?.GetValue<string>() ?? "Seerr", seerr["userId"]?.GetValue<int>() ?? 0);
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException or UriFormatException) { return null; }
    }

    /// <summary>When last changed anywhere (ISO 8601, as the Apple app writes it); the start of time for never.</summary>
    private static DateTimeOffset Updated(JsonObject secrets) =>
        secrets["updated"]?.GetValue<string>() is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : DateTimeOffset.MinValue;

    private async Task WriteServerAsync()
    {
        if (client is not { UserId: { } userId } jellyfin) return;
        try { await jellyfin.SaveAppSettingsAsync(new Dictionary<string, string> { ["trackers"] = TrackerVault.Seal(secrets, userId) }).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or JellyfinException or TaskCanceledException or JsonException) { }
    }

    // ---- This computer's copy ----

    private static string LocalKey(string owner) => "trackers|" + owner;

    private static JsonObject? Local(string owner)
    {
        try
        {
            string? text = null;
            if (OperatingSystem.IsWindows()) text = CredentialStore.Load(LocalKey(owner)) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;
            else if (SecretServiceStore.IsAvailable) text = SecretServiceStore.Load(LocalKey(owner));
            return text is null ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException) { return null; }
    }

    private void KeepLocal()
    {
        if (owner is null) return;
        var text = secrets.ToJsonString();
        if (OperatingSystem.IsWindows()) CredentialStore.Save(Encoding.UTF8.GetBytes(text), LocalKey(owner));
        else if (SecretServiceStore.IsAvailable) SecretServiceStore.Save(text, LocalKey(owner));
    }
}
