using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Aster.Core.Seerr;

/// <summary>
/// The viewer's accounts (tracking sites, and Seerr) as every Aster app keeps
/// them on the media server: scrambled in Aster's display preferences on
/// Jellyfin and Emby, under "trackers". The Apple app's TrackerVault, to the
/// byte: "aster1:" + base64(nonce ‖ ciphertext ‖ tag), AES-GCM with a key of
/// HKDF-SHA256(ikm: the phrase below, salt: the server user's id lowercased
/// without dashes, info: "aster-trackers", 32 bytes), the plaintext the
/// accounts' JSON.
///
/// The scrambling keeps tokens out of plain sight on the server. Its key is
/// in Aster's open source, so it isn't secrecy from someone with both the
/// server's data and the code.
/// </summary>
public static class TrackerVault
{
    public const string Prefix = "aster1:";
    private const string Scramble = "Aster Media Player tracker accounts, scrambled on the media server. v1";
    private const int NonceSize = 12, TagSize = 16;

    private static byte[] Key(string user)
    {
        var salt = user.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(Scramble), 32, Encoding.UTF8.GetBytes(salt), Encoding.UTF8.GetBytes("aster-trackers"));
    }

    /// <summary>The accounts' JSON, scrambled for the server.</summary>
    public static string Seal(JsonObject secrets, string user)
    {
        var plain = Encoding.UTF8.GetBytes(secrets.ToJsonString());
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(Key(user), TagSize)) aes.Encrypt(nonce, plain, cipher, tag);
        return Prefix + Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
    }

    /// <summary>The accounts from the server's blob; null for anything else, or another user's.</summary>
    public static JsonObject? Open(string? text, string user)
    {
        if (text is null || !text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var combined = Convert.FromBase64String(text[Prefix.Length..]);
            if (combined.Length < NonceSize + TagSize) return null;
            var plain = new byte[combined.Length - NonceSize - TagSize];
            using (var aes = new AesGcm(Key(user), TagSize))
                aes.Decrypt(combined.AsSpan(0, NonceSize), combined.AsSpan(NonceSize, plain.Length), combined.AsSpan(NonceSize + plain.Length), plain);
            return JsonNode.Parse(plain) as JsonObject;
        }
        catch (Exception error) when (error is FormatException or CryptographicException or System.Text.Json.JsonException) { return null; }
    }
}
