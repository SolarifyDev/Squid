using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Binds a REST fetch to the Octopus user whose API key started it. The key itself is never stored: the cursor keeps
/// the user id from <c>users/me</c> and an HMAC of the key under this Squid server's master key. A chunk sent with a
/// key of another Octopus user is refused; a different key of the same user is accepted and reported as changed, so
/// the caller can check that key's permissions again.
/// </summary>
public static class OctopusRestKeyBinding
{
    /// <summary>Domain separator inside the HMAC; persisted through <see cref="OctopusImportHistoryFetchDto.KeyHmac"/> — pinned.</summary>
    public const string HmacPurpose = "octopus-import-api-key";

    public const string UserPinnedField = "user";

    /// <summary>HMAC-SHA256(master key, purpose + "\n" + trimmed key) as lowercase hex.</summary>
    public static string Fingerprint(byte[] masterKey, string apiKey)
    {
        var data = Encoding.UTF8.GetBytes(HmacPurpose + "\n" + (apiKey ?? string.Empty).Trim());

        return Convert.ToHexString(HMACSHA256.HashData(masterKey ?? [], data)).ToLowerInvariant();
    }

    /// <summary>The configured master key as bytes: base64 when it decodes, its UTF-8 bytes otherwise, empty when unset.</summary>
    public static byte[] MasterKeyBytes(string masterKey)
    {
        if (string.IsNullOrWhiteSpace(masterKey)) return [];

        var buffer = new byte[masterKey.Length];

        return Convert.TryFromBase64String(masterKey.Trim(), buffer, out var written) ? buffer[..written] : Encoding.UTF8.GetBytes(masterKey);
    }

    /// <summary>Pins the user on the first chunk and returns whether the key differs from the one bound before (true on the first chunk too).</summary>
    public static bool Bind(OctopusImportHistoryFetchDto cursor, string octopusUserId, string keyFingerprint)
    {
        EnsureSameUser(cursor, octopusUserId);

        var changed = !SameFingerprint(cursor.KeyHmac, keyFingerprint);

        cursor.UserId ??= octopusUserId;
        cursor.KeyHmac = keyFingerprint;

        return changed;
    }

    private static void EnsureSameUser(OctopusImportHistoryFetchDto cursor, string octopusUserId)
    {
        if (cursor.UserId != null && !string.Equals(cursor.UserId, octopusUserId, StringComparison.OrdinalIgnoreCase))
            throw new OctopusImportHistoryServerMismatchException(UserPinnedField);
    }

    private static bool SameFingerprint(string stored, string current)
        => stored != null && current != null && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(stored), Encoding.ASCII.GetBytes(current));
}
