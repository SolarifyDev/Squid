using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shouldly;
using Squid.Message.Hardening;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.ScriptExecution;

/// <summary>
/// P1-Phase9.1 — agent-side V2 envelope symmetric decryption.
///
/// <para><b>Why this exists</b>: Phase-8.6 introduced the V2 sensitive-variable
/// envelope (<c>V2__</c> + AES-256-GCM + 600k PBKDF2-SHA256) on the server +
/// Calamari sides for the <c>sensitiveVariables.json</c> file format. The
/// Tentacle's output-masking helper
/// (<see cref="Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor"/>) was
/// missed and could only decrypt the V1 (<c>IV__</c> + AES-128-CBC) form. When
/// the server emitted V2 to the workspace, the Tentacle's masking pass swallowed
/// the parse error as a Debug log and returned <c>HashSet&lt;string&gt;()</c>
/// — meaning <b>sensitive values were printed verbatim into agent stdout/stderr</b>.
/// This file pins the dual-format contract so the symmetry never drifts again.</para>
///
/// <para><b>Pattern source</b>: mirrors
/// <c>Squid.Calamari.Tests.Calamari.Variables.SensitiveVariableDecryptorDualFormatTests</c>
/// — the V1/V2 envelope formats and KDF parameters MUST match across server,
/// Calamari, and Tentacle. If you change one, all three must change in lockstep
/// or the masking pass silently breaks again.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class SensitiveVariableDecryptorDualFormatTests : IDisposable
{
    private const string TestPassword = "phase9-1-tentacle-mask-pass";
    private readonly string _workDir;

    public SensitiveVariableDecryptorDualFormatTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "squid-tentacle-mask-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch { /* best-effort */ }
    }

    // ── Operator-facing env var name pinned (Rule 8) ─────────────────────────

    [Fact]
    public void LegacyAcceptEnforcementEnvVar_ConstantNamePinned()
    {
        // The agent-side env var name MUST match Calamari's so an operator can
        // flip ONE env on the deploy host and harden BOTH masking + Calamari
        // decryption together. Drift here would silently leave masking accepting
        // V1 even after Calamari is locked to strict.
        Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar
            .ShouldBe("SQUID_SENSITIVE_VAR_DECRYPT_LEGACY_ACCEPT");
    }

    // ── V2 — masking extracts values from V2 envelopes (the Phase-8.6 gap) ──

    [Fact]
    public void ExtractSensitiveValues_V2Envelope_ExtractsAllValues()
    {
        // The bug this guards: pre-Phase-9.1, V2 envelopes hit the V1-only
        // decryptor → InvalidOperationException("missing IV prefix") → swallowed
        // as Debug → return empty set → no masking → SECRETS printed verbatim.
        WriteSensitiveFile(_workDir, EncryptV2Manually(TestPassword, JsonOf("dbPwd", "s3cret-V2", "apiKey", "K3Y-V2")));
        WriteKeyFile(_workDir, TestPassword);

        var values = Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.ExtractSensitiveValues(_workDir);

        values.ShouldContain("s3cret-V2");
        values.ShouldContain("K3Y-V2");
    }

    [Fact]
    public void ExtractSensitiveValues_V1Envelope_ExtractsAllValues_BackwardCompat()
    {
        // V1 must keep working in the default Warn mode — operator's mid-rolling
        // upgrade fleet still emits V1 sometimes, masking must not regress.
        WriteSensitiveFile(_workDir, EncryptV1Manually(TestPassword, JsonOf("dbPwd", "s3cret-V1", "apiKey", "K3Y-V1")));
        WriteKeyFile(_workDir, TestPassword);

        var values = Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.ExtractSensitiveValues(_workDir);

        values.ShouldContain("s3cret-V1");
        values.ShouldContain("K3Y-V1");
    }

    [Fact]
    public void ExtractSensitiveValues_StrictMode_RejectsV1_ReturnsEmptyButLogs()
    {
        // Strict mode means operator has confirmed fleet on v1.7+ and only V2
        // should be in flight. V1 here = downgrade attack OR unexpectedly old
        // server. Masking pass returns EMPTY (best-effort, doesn't crash deploy)
        // but the rejection is logged.
        WriteSensitiveFile(_workDir, EncryptV1Manually(TestPassword, JsonOf("dbPwd", "should-not-appear")));
        WriteKeyFile(_workDir, TestPassword);

        var previous = Environment.GetEnvironmentVariable(
            Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar, "strict");

        try
        {
            var values = Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.ExtractSensitiveValues(_workDir);

            // Best-effort masking: even in strict, we don't WANT to crash a
            // running deploy. We just refuse to extract V1 values, and log.
            values.ShouldBeEmpty(customMessage:
                "Strict mode must not silently extract V1 values — that defeats the downgrade-defence purpose.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar, previous);
        }
    }

    [Fact]
    public void ExtractSensitiveValues_V2_TamperedCiphertext_ReturnsEmpty()
    {
        // GCM tag mismatch on tampered ciphertext: the whole point of upgrading
        // V1→V2 is integrity. Masking pass returns empty (defensive — bad cipher
        // text should never resurrect as plaintext).
        var ciphertext = EncryptV2Manually(TestPassword, JsonOf("dbPwd", "intact"));
        ciphertext[40] ^= 0x01;  // flip a byte in the ciphertext region
        WriteSensitiveFile(_workDir, ciphertext);
        WriteKeyFile(_workDir, TestPassword);

        var values = Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.ExtractSensitiveValues(_workDir);

        values.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractSensitiveValues_NoFile_ReturnsEmpty()
    {
        // Defensive: missing file is the common case (no sensitive vars in
        // deploy) — must NOT throw.
        var values = Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.ExtractSensitiveValues(_workDir);
        values.ShouldBeEmpty();
    }

    [Fact]
    public void ExtractSensitiveValues_UnknownPrefix_ReturnsEmpty()
    {
        // Garbage envelope: defensive, log and skip — not a deploy-breaker.
        var garbage = Encoding.UTF8.GetBytes("XYZ__not-a-known-format-marker");
        WriteSensitiveFile(_workDir, garbage);
        WriteKeyFile(_workDir, TestPassword);

        var values = Squid.Tentacle.ScriptExecution.SensitiveVariableDecryptor.ExtractSensitiveValues(_workDir);
        values.ShouldBeEmpty();
    }

    // ── Helpers — mirror Calamari's helpers exactly so both crypto sides drift
    //              in lockstep. If server changes, BOTH test files break loud.

    private static void WriteSensitiveFile(string workDir, byte[] cipherBytes) =>
        File.WriteAllBytes(Path.Combine(workDir, "sensitiveVariables.json"), cipherBytes);

    private static void WriteKeyFile(string workDir, string password) =>
        File.WriteAllText(Path.Combine(workDir, "sensitiveVariables.json.key"), password);

    private static string JsonOf(params string[] keysAndValues)
    {
        var dict = new Dictionary<string, string>();

        for (var i = 0; i < keysAndValues.Length; i += 2)
            dict[keysAndValues[i]] = keysAndValues[i + 1];

        return JsonSerializer.Serialize(dict);
    }

    private static byte[] EncryptV1Manually(string password, string plaintext)
    {
        var prefix = "IV__"u8.ToArray();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;

#pragma warning disable SYSLIB0041
        using var kdf = new Rfc2898DeriveBytes(password, Encoding.UTF8.GetBytes("SquidDep"), 1000);
#pragma warning restore SYSLIB0041
        aes.Key = kdf.GetBytes(16);

        using var transform = aes.CreateEncryptor();
        using var stream = new MemoryStream();
        stream.Write(prefix, 0, prefix.Length);
        stream.Write(aes.IV, 0, aes.IV.Length);

        using (var cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Write))
            cryptoStream.Write(plainBytes, 0, plainBytes.Length);

        return stream.ToArray();
    }

    private static byte[] EncryptV2Manually(string password, string plaintext)
    {
        const int v2Iters = 600_000;
        var prefix = "V2__"u8.ToArray();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);

        using var kdf = new Rfc2898DeriveBytes(password, salt, v2Iters, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);

        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var output = new byte[prefix.Length + salt.Length + nonce.Length + ciphertext.Length + tag.Length];
        var offset = 0;

        Buffer.BlockCopy(prefix,     0, output, offset, prefix.Length);     offset += prefix.Length;
        Buffer.BlockCopy(salt,       0, output, offset, salt.Length);       offset += salt.Length;
        Buffer.BlockCopy(nonce,      0, output, offset, nonce.Length);      offset += nonce.Length;
        Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length); offset += ciphertext.Length;
        Buffer.BlockCopy(tag,        0, output, offset, tag.Length);

        return output;
    }
}
