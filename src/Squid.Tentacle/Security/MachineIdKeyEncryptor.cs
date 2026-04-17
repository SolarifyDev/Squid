using System.Security.Cryptography;
using System.Text;

namespace Squid.Tentacle.Security;

/// <summary>
/// Cross-platform machine-key encryptor. Uses AES-256-GCM with a key derived
/// from the host's stable machine identifier (<c>/etc/machine-id</c> on Linux,
/// <c>IOPlatformUUID</c>-equivalent on macOS, registry MachineGuid on Windows)
/// stretched through PBKDF2-SHA256. Payload format:
///
///   "v1:" + base64(nonce || ciphertext || tag)
///
/// Where nonce is 12 bytes and tag is 16 bytes. The leading version tag lets
/// us introduce a new derivation (e.g. DPAPI on Windows) without breaking
/// existing stored payloads.
/// </summary>
public sealed class MachineIdKeyEncryptor : IMachineKeyEncryptor
{
    private const string VersionPrefix = "v1:";
    private const int Pbkdf2Iterations = 100_000;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("squid-tentacle-machine-key-salt-v1");

    private readonly byte[] _key;

    public MachineIdKeyEncryptor() : this(MachineIdProvider.Read()) { }

    public MachineIdKeyEncryptor(string machineId)
    {
        if (string.IsNullOrEmpty(machineId))
            throw new ArgumentException("Machine id is required to derive encryption key", nameof(machineId));

        _key = DeriveKey(machineId);
    }

    public string Protect(string plaintext)
    {
        if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var bundled = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, bundled, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, bundled, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, bundled, NonceSize + ciphertext.Length, TagSize);

        return VersionPrefix + Convert.ToBase64String(bundled);
    }

    public string Unprotect(string cipherText)
    {
        if (cipherText == null) throw new ArgumentNullException(nameof(cipherText));
        if (!cipherText.StartsWith(VersionPrefix, StringComparison.Ordinal))
            throw new FormatException($"Cipher text missing '{VersionPrefix}' version prefix — cannot decrypt");

        var bundled = Convert.FromBase64String(cipherText[VersionPrefix.Length..]);

        if (bundled.Length < NonceSize + TagSize)
            throw new FormatException("Cipher text is shorter than the minimum nonce+tag header");

        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var ciphertext = new byte[bundled.Length - NonceSize - TagSize];

        Buffer.BlockCopy(bundled, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(bundled, NonceSize, ciphertext, 0, ciphertext.Length);
        Buffer.BlockCopy(bundled, NonceSize + ciphertext.Length, tag, 0, TagSize);

        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(string machineId)
    {
        using var pbkdf2 = new Rfc2898DeriveBytes(machineId, Salt, Pbkdf2Iterations, HashAlgorithmName.SHA256);
        return pbkdf2.GetBytes(KeySize);
    }
}
