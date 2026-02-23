using System.Security.Cryptography;
using System.Text;

namespace Squid.Core.Services.Common;

/// <summary>
/// Encrypts sensitive variable payloads using the native Squid file format:
/// AES-128-CBC with PBKDF2-derived key, prefixed with "IV__" + IV bytes.
/// </summary>
public class SquidVariableEncryption
{
    private const int PasswordSaltIterations = 1000;
    private const string SaltRaw = "SquidDep";
    private static readonly byte[] PasswordPaddingSalt = Encoding.UTF8.GetBytes(SaltRaw);
    private static readonly byte[] IvPrefix = "IV__"u8.ToArray();

    private readonly byte[] _key;

    public SquidVariableEncryption(string password)
    {
        _key = GetEncryptionKey(password);
    }

    public byte[] Encrypt(string plaintext)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plaintext);
        using var algorithm = GetCryptoProvider();
        using var cryptoTransform = algorithm.CreateEncryptor();
        using var stream = new MemoryStream();

        stream.Write(IvPrefix, 0, IvPrefix.Length);
        stream.Write(algorithm.IV, 0, algorithm.IV.Length);

        using (var cryptoStream = new CryptoStream(stream, cryptoTransform, CryptoStreamMode.Write))
        {
            cryptoStream.Write(plainTextBytes, 0, plainTextBytes.Length);
        }

        return stream.ToArray();
    }

    private Aes GetCryptoProvider(byte[]? iv = null)
    {
        var provider = new AesCryptoServiceProvider
        {
            Mode = CipherMode.CBC,
            Padding = PaddingMode.PKCS7,
            KeySize = 128,
            BlockSize = 128,
            Key = _key
        };

        if (iv != null)
            provider.IV = iv;

        return provider;
    }

    public static byte[] GetEncryptionKey(string encryptionPassword)
    {
        var passwordGenerator = new Rfc2898DeriveBytes(encryptionPassword, PasswordPaddingSalt, PasswordSaltIterations);
        return passwordGenerator.GetBytes(16);
    }
}
