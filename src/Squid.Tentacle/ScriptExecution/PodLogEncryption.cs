using System.Security.Cryptography;
using System.Text;

namespace Squid.Tentacle.ScriptExecution;

public static class PodLogEncryption
{
    private const string EncryptedLinePrefix = "SQUID_ENC|";
    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int Pbkdf2Iterations = 10000;

    public static byte[] DeriveLogEncryptionKey(byte[] machineKey, string ticketId)
    {
        var salt = Encoding.UTF8.GetBytes(ticketId);
        return Rfc2898DeriveBytes.Pbkdf2(machineKey, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, KeyLength);
    }

    public static string EncryptLine(string line, byte[] key)
    {
        var nonce = new byte[NonceLength];
        RandomNumberGenerator.Fill(nonce);

        var plaintext = Encoding.UTF8.GetBytes(line);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagLength];

        using var aes = new AesGcm(key, TagLength);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var combined = new byte[NonceLength + ciphertext.Length + TagLength];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceLength);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceLength, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceLength + ciphertext.Length, TagLength);

        return EncryptedLinePrefix + Convert.ToHexString(combined);
    }

    public static (bool Success, string Plaintext) TryDecryptLine(string line, byte[] key)
    {
        if (!IsEncryptedLine(line))
            return (false, null);

        try
        {
            var hex = line.Substring(EncryptedLinePrefix.Length);
            var combined = Convert.FromHexString(hex);

            if (combined.Length < NonceLength + TagLength)
                return (false, null);

            var nonce = combined.AsSpan(0, NonceLength);
            var ciphertextLength = combined.Length - NonceLength - TagLength;
            var ciphertext = combined.AsSpan(NonceLength, ciphertextLength);
            var tag = combined.AsSpan(NonceLength + ciphertextLength, TagLength);

            var plaintext = new byte[ciphertextLength];

            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

            return (true, Encoding.UTF8.GetString(plaintext));
        }
        catch
        {
            return (false, null);
        }
    }

    public static bool IsEncryptedLine(string line)
        => line != null && line.StartsWith(EncryptedLinePrefix, StringComparison.Ordinal);
}
