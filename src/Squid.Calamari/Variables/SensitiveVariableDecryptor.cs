using System.Security.Cryptography;
using System.Text;

namespace Squid.Calamari.Variables;

/// <summary>
/// Decrypts sensitive variables encrypted with CalamariCompatibleEncryption.
/// AES-128-CBC, RFC2898 key derivation, IV prefixed with "IV__" marker.
/// </summary>
public class SensitiveVariableDecryptor
{
    private const int PasswordSaltIterations = 1000;
    private const string SaltRaw = "SquidDep";
    private static readonly byte[] PasswordPaddingSalt = Encoding.UTF8.GetBytes(SaltRaw);
    private static readonly byte[] IvPrefix = "IV__"u8.ToArray();

    private readonly byte[] _key;

    public SensitiveVariableDecryptor(string password)
    {
        _key = DeriveKey(password);
    }

    public string Decrypt(byte[] cipherData)
    {
        if (!StartsWithIvPrefix(cipherData))
            throw new InvalidOperationException("Invalid encrypted data: missing IV prefix");

        var iv = cipherData[IvPrefix.Length..(IvPrefix.Length + 16)];
        var encrypted = cipherData[(IvPrefix.Length + 16)..];

        using var aes = CreateAes(iv);
        using var transform = aes.CreateDecryptor();
        using var stream = new MemoryStream(encrypted);
        using var cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static bool StartsWithIvPrefix(byte[] data)
    {
        if (data.Length < IvPrefix.Length)
            return false;

        for (var i = 0; i < IvPrefix.Length; i++)
        {
            if (data[i] != IvPrefix[i])
                return false;
        }

        return true;
    }

    private Aes CreateAes(byte[] iv)
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = _key;
        aes.IV = iv;
        return aes;
    }

    private static byte[] DeriveKey(string password)
    {
        var generator = new Rfc2898DeriveBytes(password, PasswordPaddingSalt, PasswordSaltIterations);
        return generator.GetBytes(16);
    }
}
