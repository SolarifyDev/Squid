using System.Security.Cryptography;
using System.Text;

namespace Squid.Core.Services.OctopusImport.Secrets;

/// <summary>
/// The Octopus "Export Projects" value cipher: AES-128-CBC/PKCS7 with a key derived by
/// PBKDF2-SHA1(password, salt "Octopuss", 1000 iterations). Each encrypted value is serialized
/// as <c>base64(ciphertext)|base64(iv)</c>. These constants are Octopus' wire format — changing
/// any of them makes every existing export undecryptable, so they are pinned by unit tests.
/// </summary>
internal static class OctopusExportCipher
{
    public const int KeySizeBytes = 16;
    public const int IvSizeBytes = 16;
    public const int Pbkdf2Iterations = 1_000;
    public const string PasswordSalt = "Octopuss";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static byte[] DeriveKey(string password) => Rfc2898DeriveBytes.Pbkdf2(password, Encoding.UTF8.GetBytes(PasswordSalt), Pbkdf2Iterations, HashAlgorithmName.SHA1, KeySizeBytes);

    public static bool IsEncryptedValue(string value)
    {
        if (!TryGetEncryptedParts(value, out var ciphertext, out var iv)) return false;

        CryptographicOperations.ZeroMemory(ciphertext);
        CryptographicOperations.ZeroMemory(iv);

        return true;
    }

    /// <summary>Decrypts and returns the UTF-8 plaintext bytes. Callers own (and must zero) the result.</summary>
    public static bool TryDecryptBytes(string encryptedValue, byte[] key, out byte[] plaintext)
    {
        plaintext = null;

        if (!TryGetEncryptedParts(encryptedValue, out var ciphertext, out var iv)) return false;

        try
        {
            plaintext = Transform(ciphertext, iv, key);

            if (IsValidUtf8(plaintext)) return true;

            CryptographicOperations.ZeroMemory(plaintext);
            plaintext = null;

            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(iv);
        }
    }

    public static bool TryDecrypt(string encryptedValue, byte[] key, out string plaintext)
    {
        plaintext = null;

        if (!TryDecryptBytes(encryptedValue, key, out var bytes)) return false;

        try
        {
            plaintext = StrictUtf8.GetString(bytes);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static byte[] Transform(byte[] ciphertext, byte[] iv, byte[] key)
    {
        using var aes = Aes.Create();

        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();

        return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
    }

    private static bool IsValidUtf8(byte[] bytes)
    {
        try
        {
            var decoder = StrictUtf8.GetDecoder();
            Span<char> chars = stackalloc char[256];
            var offset = 0;

            while (offset < bytes.Length)
            {
                decoder.Convert(bytes.AsSpan(offset), chars, flush: false, out var bytesUsed, out _, out _);

                if (bytesUsed == 0) return false;

                offset += bytesUsed;
            }

            decoder.Convert(ReadOnlySpan<byte>.Empty, chars, flush: true, out _, out _, out _);

            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryGetEncryptedParts(string encryptedValue, out byte[] ciphertext, out byte[] iv)
    {
        ciphertext = null;
        iv = null;

        if (string.IsNullOrWhiteSpace(encryptedValue)) return false;

        var separatorIndex = encryptedValue.IndexOf('|');

        if (separatorIndex <= 0 || separatorIndex != encryptedValue.LastIndexOf('|')) return false;

        try
        {
            ciphertext = Convert.FromBase64String(encryptedValue[..separatorIndex]);
            iv = Convert.FromBase64String(encryptedValue[(separatorIndex + 1)..]);
        }
        catch (FormatException)
        {
            ZeroAndClear(ref ciphertext, ref iv);
            return false;
        }

        if (ciphertext.Length > 0 && ciphertext.Length % 16 == 0 && iv.Length == IvSizeBytes) return true;

        ZeroAndClear(ref ciphertext, ref iv);

        return false;
    }

    private static void ZeroAndClear(ref byte[] ciphertext, ref byte[] iv)
    {
        if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
        if (iv != null) CryptographicOperations.ZeroMemory(iv);

        ciphertext = null;
        iv = null;
    }
}
