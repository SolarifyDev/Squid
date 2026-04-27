using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Squid.Message.Hardening;

namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Extracts sensitive variable values from workspace files for output masking.
///
/// <para><b>Dual-format reader (P1-Phase9.1)</b> — symmetry with
/// <c>Squid.Calamari.Variables.SensitiveVariableDecryptor</c> and the server-side
/// emit path. The Tentacle's masking pass MUST decrypt the same envelope formats
/// that the server / Calamari side emits, otherwise sensitive values flow into
/// agent stdout/stderr unredacted.</para>
///
/// <list type="bullet">
///   <item><b>V1 (legacy)</b>: <c>IV__</c> + AES-128-CBC, PBKDF2-SHA1, 1000 iters,
///         fixed salt <c>"SquidDep"</c>. Pre-Phase-8 servers emit this. Mode-gated
///         via <see cref="LegacyAcceptEnforcementEnvVar"/> so an operator can
///         lock down once the fleet is on v1.7+.</item>
///   <item><b>V2 (new)</b>: <c>V2__</c> + salt(16) + nonce(12) + AES-256-GCM
///         ciphertext + tag(16). Per-payload random salt, PBKDF2-SHA256 at 600k
///         iters (OWASP 2023). Authenticated encryption — tag mismatch on a
///         tampered envelope yields <see cref="CryptographicException"/>.</item>
/// </list>
///
/// <para>Output-masking is best-effort: if extraction fails for any reason
/// (missing file, wrong key, V2 tag mismatch, V1 rejected by Strict mode), this
/// helper returns an empty set and logs at Debug. Failure here must NOT crash a
/// running deploy.</para>
/// </summary>
internal static class SensitiveVariableDecryptor
{
    /// <summary>
    /// Env var that selects the enforcement mode for accepting V1 (legacy)
    /// envelopes. Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>.
    ///
    /// <para>Default (unset / blank) is <see cref="EnforcementMode.Warn"/> —
    /// accept V1 with a structured Serilog warning. Operator flips to
    /// <c>strict</c> once the fleet is confirmed emitting only V2; a V1 payload
    /// after that point suggests an unexpected old server or a downgrade attack
    /// and gets rejected (returns empty mask set, logs the rejection).</para>
    ///
    /// <para><b>Pinned literal — must match Calamari</b>: same env var on the
    /// deploy host should harden BOTH the Tentacle masking pass and the Calamari
    /// decryption pass in lockstep. Drift here would silently leave masking
    /// accepting V1 even after Calamari is locked down. See
    /// <c>SensitiveVariableDecryptorDualFormatTests.LegacyAcceptEnforcementEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string LegacyAcceptEnforcementEnvVar = "SQUID_SENSITIVE_VAR_DECRYPT_LEGACY_ACCEPT";

    // ── V1 format constants (mirror Calamari V1) ─────────────────────────────
    private const int V1PasswordSaltIterations = 1000;
    private static readonly byte[] V1PasswordPaddingSalt = Encoding.UTF8.GetBytes("SquidDep");
    private static readonly byte[] V1Prefix = "IV__"u8.ToArray();
    private const int V1KeyLengthBytes = 16;
    private const int V1IvLengthBytes = 16;

    // ── V2 format constants (mirror Calamari V2 + server V2) ─────────────────
    private static readonly byte[] V2Prefix = "V2__"u8.ToArray();
    private const int V2SaltLengthBytes = 16;
    private const int V2NonceLengthBytes = 12;
    private const int V2TagLengthBytes = 16;
    private const int V2KeyLengthBytes = 32;
    private const int V2PasswordIterations = 600_000;

    internal static HashSet<string> ExtractSensitiveValues(string workDir)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            var sensitiveFile = Path.Combine(workDir, "sensitiveVariables.json");
            var keyFile = sensitiveFile + ".key";

            if (!File.Exists(sensitiveFile) || !File.Exists(keyFile))
                return result;

            var password = File.ReadAllText(keyFile).Trim();
            var cipherData = File.ReadAllBytes(sensitiveFile);
            var mode = EnforcementModeReader.Read(LegacyAcceptEnforcementEnvVar);
            var json = Decrypt(cipherData, password, mode);
            var variables = JsonSerializer.Deserialize<Dictionary<string, string>>(json);

            if (variables == null)
                return result;

            foreach (var value in variables.Values)
            {
                if (!string.IsNullOrEmpty(value))
                    result.Add(value);
            }
        }
        catch (Exception ex)
        {
            // Best-effort masking: log + return empty. Includes V2 tag mismatch
            // (tampered envelope), V1 rejected by Strict mode, garbage prefix,
            // and bad JSON after decrypt. None of these should crash a deploy.
            Log.Debug(ex, "Failed to extract sensitive values for masking from {WorkDir}", workDir);
        }

        return result;
    }

    private static string Decrypt(byte[] cipherData, string password, EnforcementMode mode)
    {
        if (cipherData == null) throw new ArgumentNullException(nameof(cipherData));

        if (StartsWith(cipherData, V2Prefix))
            return DecryptV2(cipherData, password);

        if (StartsWith(cipherData, V1Prefix))
            return DecryptV1WithModeGuard(cipherData, password, mode);

        throw new InvalidOperationException(
            "Invalid encrypted data: missing V2__/IV__ prefix. Ciphertext does not match any " +
            "known Squid sensitive-variable envelope format.");
    }

    private static string DecryptV1WithModeGuard(byte[] cipherData, string password, EnforcementMode mode)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return DecryptV1(cipherData, password);

            case EnforcementMode.Warn:
                Log.Warning(
                    "Sensitive-variable payload uses LEGACY V1 crypto (AES-128-CBC, no MAC). " +
                    "Output masking accepting for rolling-upgrade compatibility. Once the server fleet " +
                    "is on v1.7+ and emits only V2, set {EnvVar}=strict to refuse legacy downgrade " +
                    "attempts. Default mode is Warn (this message).",
                    LegacyAcceptEnforcementEnvVar);
                return DecryptV1(cipherData, password);

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    "LEGACY V1 sensitive-variable envelope rejected in Strict mode. The server fleet " +
                    "should be emitting V2 (AES-256-GCM) — a V1 payload here suggests either an " +
                    "unexpectedly old server or a downgrade attack. Output masking is intentionally " +
                    "skipping this payload. To unblock during a migration window, set " +
                    $"{LegacyAcceptEnforcementEnvVar}=warn (accept + log) or " +
                    $"{LegacyAcceptEnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private static string DecryptV1(byte[] cipherData, string password)
    {
        if (cipherData.Length < V1Prefix.Length + V1IvLengthBytes)
            throw new InvalidOperationException("Invalid V1 encrypted data: too short");

        var iv = new byte[V1IvLengthBytes];
        Buffer.BlockCopy(cipherData, V1Prefix.Length, iv, 0, iv.Length);

        var encrypted = new byte[cipherData.Length - V1Prefix.Length - iv.Length];
        Buffer.BlockCopy(cipherData, V1Prefix.Length + iv.Length, encrypted, 0, encrypted.Length);

        var key = DeriveV1Key(password);

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

    private static string DecryptV2(byte[] envelope, string password)
    {
        var prefixLen = V2Prefix.Length;
        var minLen = prefixLen + V2SaltLengthBytes + V2NonceLengthBytes + V2TagLengthBytes;

        if (envelope.Length < minLen)
            throw new InvalidOperationException(
                $"V2 envelope truncated — need at least {minLen} bytes of framing, got {envelope.Length}.");

        var salt = new byte[V2SaltLengthBytes];
        var nonce = new byte[V2NonceLengthBytes];
        var tag = new byte[V2TagLengthBytes];
        var ctLen = envelope.Length - prefixLen - V2SaltLengthBytes - V2NonceLengthBytes - V2TagLengthBytes;
        var ct = new byte[ctLen];

        Buffer.BlockCopy(envelope, prefixLen, salt, 0, V2SaltLengthBytes);
        Buffer.BlockCopy(envelope, prefixLen + V2SaltLengthBytes, nonce, 0, V2NonceLengthBytes);
        Buffer.BlockCopy(envelope, prefixLen + V2SaltLengthBytes + V2NonceLengthBytes, ct, 0, ctLen);
        Buffer.BlockCopy(envelope, prefixLen + V2SaltLengthBytes + V2NonceLengthBytes + ctLen, tag, 0, V2TagLengthBytes);

        var key = DeriveV2Key(password, salt);

        var plain = new byte[ctLen];
        using var aes = new AesGcm(key, V2TagLengthBytes);
        // CryptographicException on tag mismatch (bit-flip) or wrong key.
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
