using System.Security.Cryptography;
using System.Text;
using Squid.Calamari.Hardening;

namespace Squid.Calamari.Variables;

/// <summary>
/// Decrypts Squid sensitive-variable payloads.
///
/// <para><b>P0-B.3 (2026-04-24 audit) — dual-format reader</b>:
/// <list type="bullet">
///   <item><b>V1 (legacy)</b>: <c>IV__</c> + IV(16) + AES-128-CBC. PBKDF2-SHA1, 1000
///         iters, fixed salt <c>"SquidDep"</c>. No MAC — vulnerable to bit-flip
///         attacks. Still accepted for rolling-upgrade compatibility with
///         pre-Phase-4 servers. Operator can harden by setting
///         <see cref="LegacyAcceptEnforcementEnvVar"/>=strict once the server fleet
///         is confirmed on v1.7+ (which emits only V2).</item>
///   <item><b>V2 (new)</b>: <c>V2__</c> + salt(16) + nonce(12) + AES-256-GCM
///         ciphertext + tag(16). Per-payload random salt, PBKDF2-SHA256 at 600k iters,
///         authenticated encryption (GCM tag detects tampering). Decoded unconditionally
///         — V2 is always safe, no mode gate needed for it.</item>
/// </list>
/// </para>
///
/// <para>The envelope format + KDF parameters are mirror-pinned against the server
/// side (<c>Squid.Core.Services.Common.SquidVariableEncryption</c>) by
/// cross-project round-trip tests.</para>
/// </summary>
public class SensitiveVariableDecryptor
{
    /// <summary>
    /// Env var that selects the enforcement mode for accepting V1 (legacy) envelopes.
    /// Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>.
    ///
    /// <para>Default (unset / blank) is <see cref="EnforcementMode.Warn"/> — accept
    /// V1 with a structured warning log. Operator flips to <c>strict</c> once the
    /// server fleet is confirmed to emit only V2, hardening Calamari against any
    /// downgrade attempt (an attacker injecting V1 ciphertext to bypass the stronger
    /// modern crypto).</para>
    ///
    /// <para>Pinned literal; renaming breaks the operator-documented path.</para>
    /// </summary>
    public const string LegacyAcceptEnforcementEnvVar = "SQUID_SENSITIVE_VAR_DECRYPT_LEGACY_ACCEPT";

    // ── V1 format constants ─────────────────────────────────────────────────
    private const int V1PasswordSaltIterations = 1000;
    private const string V1SaltRaw = "SquidDep";
    private static readonly byte[] V1PasswordPaddingSalt = Encoding.UTF8.GetBytes(V1SaltRaw);
    private static readonly byte[] V1Prefix = "IV__"u8.ToArray();
    private const int V1KeyLengthBytes = 16;

    // ── V2 format constants (mirror server-side values) ─────────────────────
    private static readonly byte[] V2Prefix = "V2__"u8.ToArray();
    private const int V2SaltLengthBytes = 16;
    private const int V2NonceLengthBytes = 12;
    private const int V2TagLengthBytes = 16;
    private const int V2KeyLengthBytes = 32;
    public const int V2PasswordIterations = 600_000;

    private readonly string _password;

    public SensitiveVariableDecryptor(string password)
    {
        _password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public string Decrypt(byte[] cipherData)
    {
        var mode = EnforcementModeReader.Read(LegacyAcceptEnforcementEnvVar);
        return Decrypt(cipherData, mode);
    }

    /// <summary>
    /// Mode-explicit decrypt entry — public so unit tests can parameterise without
    /// mutating process env. Production callers go through <see cref="Decrypt(byte[])"/>.
    /// </summary>
    public string Decrypt(byte[] cipherData, EnforcementMode mode)
    {
        if (cipherData == null) throw new ArgumentNullException(nameof(cipherData));

        if (StartsWith(cipherData, V2Prefix))
            return DecryptV2(cipherData);

        if (StartsWith(cipherData, V1Prefix))
            return DecryptV1WithModeGuard(cipherData, mode);

        throw new InvalidOperationException(
            "Invalid encrypted data: missing V2__/IV__ prefix. Ciphertext does not match any " +
            "known Squid sensitive-variable envelope format.");
    }

    private string DecryptV1WithModeGuard(byte[] cipherData, EnforcementMode mode)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return DecryptV1(cipherData);

            case EnforcementMode.Warn:
                Console.Error.WriteLine(
                    $"[WARN] Sensitive-variable payload uses LEGACY V1 crypto (AES-128-CBC, no MAC). " +
                    $"Accepting for rolling-upgrade compatibility. Once the server fleet is on v1.7+ " +
                    $"and emitting only V2, set {LegacyAcceptEnforcementEnvVar}=strict on Calamari " +
                    $"hosts to refuse legacy downgrade attempts.");
                return DecryptV1(cipherData);

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    "LEGACY V1 sensitive-variable envelope rejected in Strict mode. The server fleet " +
                    "should be emitting V2 (AES-256-GCM) — a V1 payload here suggests either an " +
                    "unexpectedly old server or a downgrade attack. To unblock during a migration " +
                    $"window, set {LegacyAcceptEnforcementEnvVar}=warn (accept + log) or " +
                    $"{LegacyAcceptEnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private string DecryptV1(byte[] cipherData)
    {
        var iv = new byte[16];
        Buffer.BlockCopy(cipherData, V1Prefix.Length, iv, 0, iv.Length);

        var encrypted = new byte[cipherData.Length - V1Prefix.Length - iv.Length];
        Buffer.BlockCopy(cipherData, V1Prefix.Length + iv.Length, encrypted, 0, encrypted.Length);

        var key = DeriveV1Key(_password);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        aes.IV = iv;

        using var transform = aes.CreateDecryptor();
        using var stream = new MemoryStream(encrypted);
        using var cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private string DecryptV2(byte[] envelope)
    {
        const int prefixLen = 4;
        var saltLen = V2SaltLengthBytes;
        var nonceLen = V2NonceLengthBytes;
        var tagLen = V2TagLengthBytes;

        var minLen = prefixLen + saltLen + nonceLen + tagLen;
        if (envelope.Length < minLen)
            throw new InvalidOperationException(
                $"V2 envelope truncated — need at least {minLen} bytes of framing, got {envelope.Length}.");

        var salt = new byte[saltLen];
        var nonce = new byte[nonceLen];
        var tag = new byte[tagLen];
        var ctLen = envelope.Length - prefixLen - saltLen - nonceLen - tagLen;
        var ct = new byte[ctLen];

        Buffer.BlockCopy(envelope, prefixLen, salt, 0, saltLen);
        Buffer.BlockCopy(envelope, prefixLen + saltLen, nonce, 0, nonceLen);
        Buffer.BlockCopy(envelope, prefixLen + saltLen + nonceLen, ct, 0, ctLen);
        Buffer.BlockCopy(envelope, prefixLen + saltLen + nonceLen + ctLen, tag, 0, tagLen);

        var key = DeriveV2Key(_password, salt);

        var plain = new byte[ctLen];
        using var aes = new AesGcm(key, tagLen);
        // Will throw CryptographicException on tag mismatch (bit-flip) or wrong key.
        aes.Decrypt(nonce, ct, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] DeriveV1Key(string password)
    {
#pragma warning disable SYSLIB0041 // legacy 3-arg overload uses SHA-1 — intentional for V1 interop
        using var generator = new Rfc2898DeriveBytes(password, V1PasswordPaddingSalt, V1PasswordSaltIterations);
#pragma warning restore SYSLIB0041
        return generator.GetBytes(V1KeyLengthBytes);
    }

    private static byte[] DeriveV2Key(string password, byte[] salt)
    {
        using var generator = new Rfc2898DeriveBytes(
            password, salt, V2PasswordIterations, HashAlgorithmName.SHA256);
        return generator.GetBytes(V2KeyLengthBytes);
    }

    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length) return false;

        for (var i = 0; i < prefix.Length; i++)
            if (data[i] != prefix[i]) return false;

        return true;
    }
}
