using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Squid.Core.Services.Common;

public class CalamariCompatibleEncryption
{
    const int PasswordSaltIterations = 1000;
    const string SaltRaw = "Octopuss";
    static readonly byte[] PasswordPaddingSalt = Encoding.UTF8.GetBytes(SaltRaw);
    static readonly byte[] IvPrefix = "IV__"u8.ToArray();

    readonly byte[] key;

    public CalamariCompatibleEncryption(string password)
    {
        key = GetEncryptionKey(password);
    }

    public byte[] Encrypt(string plaintext)
    {
        var plainTextBytes = Encoding.UTF8.GetBytes(plaintext);
        using (var algorithm = GetCryptoProvider())
        using (var cryptoTransform = algorithm.CreateEncryptor())
        using (var stream = new MemoryStream())
        {
            // 写入IV前缀和IV
            stream.Write(IvPrefix, 0, IvPrefix.Length);
            stream.Write(algorithm.IV, 0, algorithm.IV.Length);
            
            // 加密数据
            using (var cs = new CryptoStream(stream, cryptoTransform, CryptoStreamMode.Write))
            {
                cs.Write(plainTextBytes, 0, plainTextBytes.Length);
            }
            
            return stream.ToArray();
        }
    }

    Aes GetCryptoProvider(byte[]? iv = null)
    {
        var provider = new AesCryptoServiceProvider
        {
            Mode = CipherMode.CBC,
            Padding = PaddingMode.PKCS7,
            KeySize = 128,
            BlockSize = 128,
            Key = key
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