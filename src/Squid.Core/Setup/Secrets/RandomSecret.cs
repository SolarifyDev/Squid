using System.Security.Cryptography;

namespace Squid.Core.Setup.Secrets;

public static class RandomSecret
{
    public static string Base64(int byteCount) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(byteCount));

    /// <summary>URL / connection-string safe (no <c>;</c>, <c>=</c>, <c>,</c>, quotes).</summary>
    public static string Alphanumeric(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

        return RandomNumberGenerator.GetString(alphabet, length);
    }
}
