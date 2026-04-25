using System.Security.Cryptography;
using System.Text;
using Squid.Message.Hardening;

namespace Squid.Core.Services.Common;

/// <summary>
/// Encrypts sensitive variable payloads for transport to Calamari.
///
/// <para><b>P0-B.3 (2026-04-24 audit) — dual-format support</b>:
/// <list type="bullet">
///   <item><b>V1 (legacy)</b>: <c>IV__</c> + IV(16) + AES-128-CBC ciphertext. Keys
///         derived via PBKDF2-SHA1, 1000 iters, fixed salt <c>"SquidDep"</c>, 16-byte
///         output. No authentication (malleable — attacker can bit-flip without
///         detection). Would fail any modern crypto review.</item>
///   <item><b>V2 (new)</b>: <c>V2__</c> + salt(16) + nonce(12) + AES-256-GCM
///         ciphertext + tag(16). Keys derived via PBKDF2-SHA256, 600_000 iters
///         (OWASP 2023), random per-payload salt, 32-byte output. Authenticated
///         encryption (GCM tag detects any tampering).</item>
/// </list>
/// </para>
///
/// <para><b>Rolling-upgrade contract</b>: the matching decryptor in Calamari
/// (<c>SensitiveVariableDecryptor</c>) accepts both V1 and V2. This server can
/// still emit V1 (default, backward-compat with pre-v1.7 agents) or V2 (modern,
/// requires v1.7+ agents) depending on the enforcement mode read from
/// <see cref="EncryptionEnforcementEnvVar"/>. Once the operator has confirmed the
/// whole fleet is on v1.7+, flipping to <c>strict</c> produces only V2 and
/// retires the weak crypto path for new payloads.</para>
/// </summary>
public class SquidVariableEncryption
{
    /// <summary>
    /// Env var that selects which format this encryptor emits. Recognised values:
    /// <c>off</c> / <c>warn</c> / <c>strict</c>.
    ///
    /// <para>Default (unset / blank) is <see cref="EnforcementMode.Warn"/> — emits
    /// legacy V1 format for rolling-upgrade compatibility with old Calamari
    /// binaries AND logs a structured Serilog warning pointing the operator at
    /// this env var. Flip to <c>strict</c> once all agents are on v1.7+ to emit
    /// modern V2.</para>
    ///
    /// <para>Pinned literal; renaming breaks the operator-documented path.</para>
    /// </summary>
    public const string EncryptionEnforcementEnvVar = "SQUID_SENSITIVE_VAR_ENCRYPTION_ENFORCEMENT";

    // ── V1 format constants (legacy — reproduced for interop only) ──────────
    private const int V1PasswordSaltIterations = 1000;
    private const string V1SaltRaw = "SquidDep";
    private static readonly byte[] V1PasswordPaddingSalt = Encoding.UTF8.GetBytes(V1SaltRaw);
    private static readonly byte[] V1Prefix = "IV__"u8.ToArray();
    private const int V1KeyLengthBytes = 16;

    // ── V2 format constants ─────────────────────────────────────────────────
    /// <summary>V2 envelope prefix — operator-visible in the ciphertext file's
    /// leading bytes. Length matches V1 prefix (4 bytes) so the prefix check is
    /// uniform across formats.</summary>
    public static readonly byte[] V2Prefix = "V2__"u8.ToArray();

    /// <summary>AES-GCM recommended nonce length.</summary>
    public const int V2NonceLengthBytes = 12;

    /// <summary>AES-GCM tag length — max value.</summary>
    public const int V2TagLengthBytes = 16;

    /// <summary>Per-payload random salt for PBKDF2.</summary>
    public const int V2SaltLengthBytes = 16;

    /// <summary>AES-256 key length after KDF.</summary>
    public const int V2KeyLengthBytes = 32;

    /// <summary>OWASP 2023 recommendation for PBKDF2-SHA256 iterations.</summary>
    public const int V2PasswordIterations = 600_000;

    private readonly string _password;

    public SquidVariableEncryption(string password)
    {
        _password = password ?? throw new ArgumentNullException(nameof(password));
    }

    /// <summary>
    /// Encrypt under the format chosen by the current enforcement mode.
    /// Default (Warn) → V1 (legacy) with a startup-style warning log.
    /// Strict → V2 (modern). Off → V1 silent.
    /// </summary>
    public byte[] Encrypt(string plaintext)
    {
        var mode = EnforcementModeReader.Read(EncryptionEnforcementEnvVar);
        return Encrypt(plaintext, mode);
    }

    /// <summary>
    /// Mode-explicit encrypt entry. Exposed <c>public</c> so unit tests exercise
    /// every branch without mutating process env. Production callers go through
    /// <see cref="Encrypt(string)"/>.
    /// </summary>
    public byte[] Encrypt(string plaintext, EnforcementMode mode)
    {
        return mode switch
        {
            EnforcementMode.Strict => EncryptV2(plaintext),
            EnforcementMode.Warn => EncryptV1WithWarning(plaintext),
            EnforcementMode.Off => EncryptV1(plaintext),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode"),
        };
    }

    private byte[] EncryptV1WithWarning(string plaintext)
    {
        // This is the LEGACY path. Emit a structured warning so the operator knows
        // they're still on weak crypto and can flip the env var once agents are
        // on v1.7+. Logging is per-encrypt, which is noisy — but sensitive-var
        // encryption happens at most once per deploy step, so volume is bounded.
        Log.Warning(
            "Sensitive-variable payload emitted with LEGACY V1 crypto (AES-128-CBC, PBKDF2-SHA1/1k, " +
            "fixed salt, no MAC). This preserves backward compat with pre-v1.7 Calamari binaries. " +
            "Once all agents are v1.7+, set {EnvVar}=strict to emit V2 (AES-256-GCM, PBKDF2-SHA256/600k, " +
            "random salt, authenticated). See Squid.Core.Services.Common.SquidVariableEncryption.",
            EncryptionEnforcementEnvVar);

        // fall through to the silent encryptor — logging done above
        return EncryptV1(plaintext);
    }

    private byte[] EncryptV1(string plaintext)
    {
        var key = DeriveV1Key(_password);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        // .NET auto-generates a random IV on Aes.Create() / key assignment. We use it as-is.

        using var transform = aes.CreateEncryptor();
        using var stream = new MemoryStream();

        stream.Write(V1Prefix, 0, V1Prefix.Length);
        stream.Write(aes.IV, 0, aes.IV.Length);

        using (var cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Write))
            cryptoStream.Write(plainBytes, 0, plainBytes.Length);

        return stream.ToArray();
    }

    private byte[] EncryptV2(string plaintext)
    {
        // V2 layout: V2__ || salt(16) || nonce(12) || ciphertext(var) || tag(16)
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var salt = RandomNumberGenerator.GetBytes(V2SaltLengthBytes);
        var nonce = RandomNumberGenerator.GetBytes(V2NonceLengthBytes);

        var key = DeriveV2Key(_password, salt);

        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[V2TagLengthBytes];

        using var aes = new AesGcm(key, V2TagLengthBytes);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var totalLength = V2Prefix.Length + salt.Length + nonce.Length + ciphertext.Length + tag.Length;
        var output = new byte[totalLength];
        var offset = 0;

        Buffer.BlockCopy(V2Prefix, 0, output, offset, V2Prefix.Length);
        offset += V2Prefix.Length;
        Buffer.BlockCopy(salt, 0, output, offset, salt.Length);
        offset += salt.Length;
        Buffer.BlockCopy(nonce, 0, output, offset, nonce.Length);
        offset += nonce.Length;
        Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length);
        offset += ciphertext.Length;
        Buffer.BlockCopy(tag, 0, output, offset, tag.Length);

        return output;
    }

    /// <summary>
    /// V1 key derivation — fixed salt "SquidDep", SHA-1, 1000 iters, 16-byte output.
    /// Reproduced for interop; not for use in any new code path.
    /// </summary>
    public static byte[] DeriveV1Key(string password)
    {
#pragma warning disable SYSLIB0041 // PBKDF2 no-iteration overload is obsolete — we pass iterations explicitly
        using var generator = new Rfc2898DeriveBytes(password, V1PasswordPaddingSalt, V1PasswordSaltIterations);
#pragma warning restore SYSLIB0041
        return generator.GetBytes(V1KeyLengthBytes);
    }

    /// <summary>
    /// V2 key derivation — per-payload random salt, SHA-256, 600k iters, 32-byte output.
    /// Exposed <c>public</c> so the Calamari-side decryptor can use the same helper via
    /// copy (pinned by cross-project tests to produce identical keys).
    /// </summary>
    public static byte[] DeriveV2Key(string password, byte[] salt)
    {
        using var generator = new Rfc2898DeriveBytes(
            password, salt, V2PasswordIterations, HashAlgorithmName.SHA256);
        return generator.GetBytes(V2KeyLengthBytes);
    }

    /// <summary>
    /// Legacy static entry kept for any caller that used the pre-refactor signature.
    /// Returns the V1 16-byte key — only correct for V1 decrypt / encrypt. New code
    /// should go through <see cref="DeriveV2Key"/>.
    /// </summary>
    public static byte[] GetEncryptionKey(string encryptionPassword) => DeriveV1Key(encryptionPassword);
}
