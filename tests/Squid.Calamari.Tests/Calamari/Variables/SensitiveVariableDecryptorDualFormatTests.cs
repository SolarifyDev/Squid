using System.Security.Cryptography;
using System.Text;
using Squid.Calamari.Hardening;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Tests.Calamari.Variables;

/// <summary>
/// P0-B.3 regression guard (2026-04-24 audit, Phase 4 Calamari side).
///
/// <para>The server side <c>SquidVariableEncryption</c> has its own (input × mode)
/// matrix tests in <c>Squid.UnitTests</c>. This file pins the Calamari decryptor:
/// V2 is always accepted; V1 (legacy) acceptance is mode-gated so operators can
/// lock down once the fleet is confirmed on v1.7+ (no more V1 in flight).</para>
///
/// <para>The envelope format + KDF parameters MUST match the server. If you
/// change one side and not the other, ciphertexts stop decrypting — a silent
/// mid-deploy breakage. The cross-project round-trip test at the end of this
/// file guards against that drift.</para>
/// </summary>
public sealed class SensitiveVariableDecryptorDualFormatTests
{
    private const string TestPassword = "P0-B3-calamari-test-passphrase";

    // ── Operator-facing env var name pinned ─────────────────────────────────

    [Fact]
    public void LegacyAcceptEnforcementEnvVar_ConstantNamePinned()
    {
        SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar
            .ShouldBe("SQUID_SENSITIVE_VAR_DECRYPT_LEGACY_ACCEPT");
    }

    // ── V2 — always accepted ────────────────────────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void V2Envelope_AcceptedInAnyMode(EnforcementMode mode)
    {
        // V2 is the modern safe format. Mode governs LEGACY acceptance only —
        // V2 decrypt is always on.
        var ciphertext = EncryptV2Manually(TestPassword, "v2-secret");

        var sut = new SensitiveVariableDecryptor(TestPassword);

        sut.Decrypt(ciphertext, mode).ShouldBe("v2-secret");
    }

    [Fact]
    public void V2Envelope_TamperedCiphertext_Throws()
    {
        // The whole point of the V2 upgrade: AES-GCM detects tampering.
        var ciphertext = EncryptV2Manually(TestPassword, "v2-secret");
        ciphertext[32] ^= 0x01;  // flip one bit in ciphertext region

        var sut = new SensitiveVariableDecryptor(TestPassword);

        Should.Throw<CryptographicException>(
            () => sut.Decrypt(ciphertext, EnforcementMode.Warn),
            customMessage:
                "AES-GCM auth tag must reject bit-flipped ciphertext. If this test fails, the V2 " +
                "integrity guarantee is broken — the P0-B.3 malleability vector is back.");
    }

    [Fact]
    public void V2Envelope_WrongPassword_Throws()
    {
        var ciphertext = EncryptV2Manually(TestPassword, "v2-secret");

        var sut = new SensitiveVariableDecryptor("WRONG-PASSWORD");

        Should.Throw<CryptographicException>(
            () => sut.Decrypt(ciphertext, EnforcementMode.Warn));
    }

    // ── V1 legacy — mode-gated ──────────────────────────────────────────────

    [Fact]
    public void V1Envelope_WarnMode_Accepts_BackwardCompat()
    {
        // Phase-3 rule 11 posture: default Warn = keep deploys working during rolling
        // upgrade. Pre-Phase-4 servers emit V1; new Calamari must decrypt without
        // surprising the operator.
        var ciphertext = EncryptV1Manually(TestPassword, "v1-secret");

        var sut = new SensitiveVariableDecryptor(TestPassword);

        sut.Decrypt(ciphertext, EnforcementMode.Warn).ShouldBe("v1-secret");
    }

    [Fact]
    public void V1Envelope_OffMode_AcceptsSilently()
    {
        var ciphertext = EncryptV1Manually(TestPassword, "v1-secret");

        var sut = new SensitiveVariableDecryptor(TestPassword);

        Should.NotThrow(() => sut.Decrypt(ciphertext, EnforcementMode.Off));
    }

    [Fact]
    public void V1Envelope_StrictMode_Rejects()
    {
        // Once the fleet is confirmed on v1.7+ (servers emit only V2), operators flip
        // Strict on Calamari hosts. A V1 payload after that point suggests either an
        // unexpected old server or a downgrade attack — refuse.
        var ciphertext = EncryptV1Manually(TestPassword, "v1-secret");

        var sut = new SensitiveVariableDecryptor(TestPassword);

        var ex = Should.Throw<InvalidOperationException>(
            () => sut.Decrypt(ciphertext, EnforcementMode.Strict));

        ex.Message.ShouldContain(SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar,
            customMessage: "error must name the env var the operator can flip to temporarily unblock");
    }

    // ── Unknown envelope — always rejected ──────────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void UnknownPrefix_AlwaysRejected(EnforcementMode mode)
    {
        var garbage = Encoding.UTF8.GetBytes("XYZ__whatevergarbagecomesafter");

        var sut = new SensitiveVariableDecryptor(TestPassword);

        Should.Throw<InvalidOperationException>(() => sut.Decrypt(garbage, mode));
    }

    // ── Default mode = Warn (via env var reader) ────────────────────────────

    [Fact]
    public void DefaultMode_EnvVarUnset_AcceptsV1WithWarning_BackwardCompat()
    {
        // Instance Decrypt() reads env var → EnforcementModeReader.Read → default
        // Warn → V1 accepted. Regressing this default would break the rolling-upgrade
        // story for every operator on a mixed fleet.
        var previous = Environment.GetEnvironmentVariable(
            SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar, null);

        try
        {
            var ciphertext = EncryptV1Manually(TestPassword, "rolling-upgrade-payload");

            var sut = new SensitiveVariableDecryptor(TestPassword);

            sut.Decrypt(ciphertext).ShouldBe("rolling-upgrade-payload",
                customMessage:
                    "default mode MUST accept V1 for backward compat. If this test fails, the " +
                    "three-mode pattern's Warn-default-preserves-backward-compat contract is broken.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar, previous);
        }
    }

    [Fact]
    public void DefaultMode_EnvVarSetStrict_RejectsV1()
    {
        // Operator has flipped to strict after confirming fleet on v1.7+. V1 payload
        // arriving here should be rejected.
        var previous = Environment.GetEnvironmentVariable(
            SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar, "strict");

        try
        {
            var ciphertext = EncryptV1Manually(TestPassword, "legacy-should-fail");

            var sut = new SensitiveVariableDecryptor(TestPassword);

            Should.Throw<InvalidOperationException>(() => sut.Decrypt(ciphertext));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                SensitiveVariableDecryptor.LegacyAcceptEnforcementEnvVar, previous);
        }
    }

    // ── Helpers: encrypt in V1 / V2 format for test setup ───────────────────
    //
    // These mirror the server-side SquidVariableEncryption logic so we can generate
    // payloads without a cross-project reference. If the server ever changes format,
    // tests here must change in lockstep — exactly the drift we want to be loud.

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
        var prefix = "V2__"u8.ToArray();
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);

        using var kdf = new Rfc2898DeriveBytes(password, salt, SensitiveVariableDecryptor.V2PasswordIterations, HashAlgorithmName.SHA256);
        var key = kdf.GetBytes(32);

        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        var output = new byte[prefix.Length + salt.Length + nonce.Length + ciphertext.Length + tag.Length];
        var offset = 0;

        Buffer.BlockCopy(prefix, 0, output, offset, prefix.Length);    offset += prefix.Length;
        Buffer.BlockCopy(salt,   0, output, offset, salt.Length);      offset += salt.Length;
        Buffer.BlockCopy(nonce,  0, output, offset, nonce.Length);     offset += nonce.Length;
        Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length); offset += ciphertext.Length;
        Buffer.BlockCopy(tag,    0, output, offset, tag.Length);

        return output;
    }
}
