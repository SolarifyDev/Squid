using System;
using System.Security.Cryptography;
using System.Text;
using Squid.Core.Services.Common;
using Squid.Message.Hardening;

namespace Squid.UnitTests.Services.Common;

/// <summary>
/// P0-B.3 regression guard (2026-04-24 audit). Pre-fix,
/// <see cref="SquidVariableEncryption"/> used:
/// <list type="bullet">
///   <item>PBKDF2-SHA1 at 1000 iterations (OWASP 2023 recommends 600_000+ for SHA-256)</item>
///   <item>Hardcoded salt <c>"SquidDep"</c> — same KDF input across every payload, every
///         customer, every install. Defeats salting entirely.</item>
///   <item>AES-128 CBC with NO MAC — ciphertext is malleable; an attacker who can
///         intercept the sensitiveVariables.json blob can flip bits without detection.</item>
/// </list>
///
/// <para>Fix: new V2 envelope uses AES-256-GCM (authenticated encryption) with
/// per-payload random salt (16 B) + random nonce (12 B) + PBKDF2-SHA256 at 600k iters.
/// V1 remains readable for rolling-upgrade compatibility; server emits V1 by default
/// (<see cref="EnforcementMode.Warn"/>) and V2 under explicit
/// <c>SQUID_SENSITIVE_VAR_ENCRYPTION_ENFORCEMENT=strict</c>.</para>
///
/// <para>Follows CLAUDE.md §"Hardening Three-Mode Enforcement" so the fix is
/// non-breaking for existing fleets — operators flip to strict after upgrading all
/// agents to v1.7+.</para>
/// </summary>
public sealed class SquidVariableEncryptionDualFormatTests
{
    private const string TestPassword = "P0-B3-test-passphrase-never-production";

    [Fact]
    public void EncryptionEnforcementEnvVar_ConstantNamePinned()
    {
        // Rename breaks the operator-facing env var name. Hard-pin so a refactor is
        // a compile-visible decision.
        SquidVariableEncryption.EncryptionEnforcementEnvVar.ShouldBe("SQUID_SENSITIVE_VAR_ENCRYPTION_ENFORCEMENT");
    }

    [Fact]
    public void V2Prefix_Pinned()
    {
        // The envelope prefix is the wire protocol between server + calamari.
        // Drift here silently misroutes ciphertext to the wrong decrypt path —
        // manifests as decryption failures mid-deploy, not a clean startup error.
        SquidVariableEncryption.V2Prefix.Length.ShouldBe(4);
        Encoding.ASCII.GetString(SquidVariableEncryption.V2Prefix).ShouldBe("V2__");
    }

    // ── Strict mode: emits V2 ───────────────────────────────────────────────

    [Fact]
    public void Strict_EmitsV2Prefix()
    {
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("payload", EnforcementMode.Strict);

        var prefix = Encoding.ASCII.GetString(ciphertext, 0, 4);
        prefix.ShouldBe("V2__",
            customMessage:
                "Strict mode MUST emit V2 envelope. If a V1 prefix appears here, someone flipped " +
                "the dispatch — downgrading the crypto for every operator who opted in.");
    }

    [Fact]
    public void Strict_V2EnvelopeLayoutIsCorrect()
    {
        // V2 layout: V2__(4) || salt(16) || nonce(12) || ciphertext(var) || tag(16)
        // For a 7-byte plaintext "payload", total = 4 + 16 + 12 + 7 + 16 = 55 bytes.
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("payload", EnforcementMode.Strict);

        var expectedLength = 4 + SquidVariableEncryption.V2SaltLengthBytes
                               + SquidVariableEncryption.V2NonceLengthBytes
                               + 7
                               + SquidVariableEncryption.V2TagLengthBytes;
        ciphertext.Length.ShouldBe(expectedLength,
            customMessage:
                $"V2 envelope size mismatch. Expected {expectedLength} bytes for 7-byte plaintext; " +
                $"got {ciphertext.Length}. Layout: V2__(4) || salt(16) || nonce(12) || ct(var) || tag(16).");
    }

    [Fact]
    public void Strict_V2RandomisesSaltAndNonce()
    {
        // Two encrypts of the SAME plaintext under the SAME password must produce
        // different ciphertexts — proves salt + nonce are actually random per call.
        // Pre-fix V1 used a fixed salt (though CBC's auto-random IV made the full
        // ciphertext different anyway — but the key was identical across payloads).
        var sut = new SquidVariableEncryption(TestPassword);

        var a = sut.Encrypt("same plaintext", EnforcementMode.Strict);
        var b = sut.Encrypt("same plaintext", EnforcementMode.Strict);

        a.ShouldNotBe(b,
            customMessage:
                "two V2 encrypts of identical plaintext must differ — randomness in salt and nonce. " +
                "If this test fails, either the RNG is broken or salt/nonce got wired to a constant.");

        // Slice out the salt region (bytes 4..20) and confirm they differ.
        var saltA = new byte[SquidVariableEncryption.V2SaltLengthBytes];
        var saltB = new byte[SquidVariableEncryption.V2SaltLengthBytes];
        Buffer.BlockCopy(a, 4, saltA, 0, saltA.Length);
        Buffer.BlockCopy(b, 4, saltB, 0, saltB.Length);

        saltA.ShouldNotBe(saltB, customMessage: "per-payload salt must be unique");
    }

    // ── Warn mode (default): emits V1 for backward compat ───────────────────

    [Fact]
    public void Warn_EmitsV1Prefix_BackwardCompat()
    {
        // Phase-3 Rule-11 posture: default = Warn = backward-compat behaviour.
        // This is the KEY non-breaking contract for the rolling-upgrade story: pre-v1.7
        // Calamari binaries only understand V1, so new servers MUST keep emitting V1 by
        // default until operators confirm fleet is on v1.7+ and flip strict.
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("payload", EnforcementMode.Warn);

        var prefix = Encoding.ASCII.GetString(ciphertext, 0, 4);
        prefix.ShouldBe("IV__",
            customMessage:
                "Warn mode (default) MUST emit V1 for backward compat with old Calamari. " +
                "Regressing to V2-default breaks every operator running an old agent.");
    }

    [Fact]
    public void Off_EmitsV1Prefix_Silent()
    {
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("payload", EnforcementMode.Off);

        Encoding.ASCII.GetString(ciphertext, 0, 4).ShouldBe("IV__");
    }

    // ── V1 round-trip (via legacy key derivation) ──────────────────────────

    [Fact]
    public void V1_RoundTrip_DecryptsSuccessfully()
    {
        // Server emits V1 → decrypt using the V1 key derivation + raw AES-CBC
        // (reproduces what Calamari's SensitiveVariableDecryptor does).
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("secret-payload", EnforcementMode.Off);

        var plaintext = DecryptV1Manually(TestPassword, ciphertext);

        plaintext.ShouldBe("secret-payload");
    }

    // ── V2 round-trip ───────────────────────────────────────────────────────

    [Fact]
    public void V2_RoundTrip_DecryptsSuccessfully()
    {
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("secret-payload", EnforcementMode.Strict);

        var plaintext = DecryptV2Manually(TestPassword, ciphertext);

        plaintext.ShouldBe("secret-payload",
            customMessage:
                "V2 round-trip must recover the original plaintext. If this fails, the encrypt " +
                "side and the format-spec's expected decrypt side are out of sync.");
    }

    [Fact]
    public void V2_TagIntegrity_BitFlipDetected()
    {
        // THE reason for V2. AES-GCM authenticates the ciphertext. A single bit flip
        // in the encrypted region MUST cause decryption to throw. Pre-fix V1 (CBC +
        // no MAC) silently gave back garbage plaintext instead.
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("secret-payload", EnforcementMode.Strict);

        // Flip a bit in the ciphertext region (just after V2__(4) + salt(16) + nonce(12) = 32).
        ciphertext[32] ^= 0x01;

        Should.Throw<CryptographicException>(
            () => DecryptV2Manually(TestPassword, ciphertext),
            customMessage:
                "V2 AES-GCM MUST detect ciphertext tampering via the auth tag. If this test fails, " +
                "integrity is broken and the P0-B.3 malleability vector is back.");
    }

    [Fact]
    public void V2_WrongPassword_Throws()
    {
        var sut = new SquidVariableEncryption(TestPassword);

        var ciphertext = sut.Encrypt("secret-payload", EnforcementMode.Strict);

        Should.Throw<CryptographicException>(
            () => DecryptV2Manually("WRONG-PASSWORD", ciphertext),
            customMessage: "V2 decrypt with wrong password must throw at the AES-GCM auth check");
    }

    [Fact]
    public void V2_IterationCountPinned_At_OWASP_2023_Recommendation()
    {
        // Pinning the iteration count in a test. Lowering it would silently weaken the
        // KDF; raising it would break fleet performance at deploy-time. Either change
        // should be a deliberate compile-visible decision, not a drift.
        SquidVariableEncryption.V2PasswordIterations.ShouldBe(600_000,
            customMessage:
                "V2 PBKDF2 iteration count changed. OWASP 2023 guidance is ≥ 600_000 for SHA-256. " +
                "Raising is fine (measure impact); lowering weakens the KDF materially.");
    }

    // ── Default enforcement mode = Warn (backward-compat pinning) ──────────

    [Fact]
    public void DefaultMode_EnvVarUnset_EmitsV1_BackwardCompat()
    {
        // Instance Encrypt() reads env var → EnforcementModeReader.Read → default
        // Warn → V1 output. If anything on that chain flips, deploys break silently.
        var previous = Environment.GetEnvironmentVariable(SquidVariableEncryption.EncryptionEnforcementEnvVar);
        Environment.SetEnvironmentVariable(SquidVariableEncryption.EncryptionEnforcementEnvVar, null);

        try
        {
            var sut = new SquidVariableEncryption(TestPassword);

            var ciphertext = sut.Encrypt("payload");  // uses the parameterless overload

            Encoding.ASCII.GetString(ciphertext, 0, 4).ShouldBe("IV__",
                customMessage:
                    "unset env var MUST default to Warn (emit V1 for backward compat). If V2 appears " +
                    "here, Rule-11 default-is-backward-compat was broken and rolling upgrade is broken.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SquidVariableEncryption.EncryptionEnforcementEnvVar, previous);
        }
    }

    // ── Helper — V1 manual decrypt (mirrors Calamari's SensitiveVariableDecryptor) ──

    private static string DecryptV1Manually(string password, byte[] ciphertext)
    {
        var prefix = "IV__"u8.ToArray();
        var iv = new byte[16];
        Buffer.BlockCopy(ciphertext, prefix.Length, iv, 0, iv.Length);

        var encrypted = new byte[ciphertext.Length - prefix.Length - iv.Length];
        Buffer.BlockCopy(ciphertext, prefix.Length + iv.Length, encrypted, 0, encrypted.Length);

        var key = SquidVariableEncryption.DeriveV1Key(password);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.KeySize = 128;
        aes.BlockSize = 128;
        aes.Key = key;
        aes.IV = iv;

        using var transform = aes.CreateDecryptor();
        using var stream = new System.IO.MemoryStream(encrypted);
        using var cryptoStream = new CryptoStream(stream, transform, CryptoStreamMode.Read);
        using var reader = new System.IO.StreamReader(cryptoStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ── Helper — V2 manual decrypt ──────────────────────────────────────────

    private static string DecryptV2Manually(string password, byte[] envelope)
    {
        const int prefixLen = 4;
        var saltLen = SquidVariableEncryption.V2SaltLengthBytes;
        var nonceLen = SquidVariableEncryption.V2NonceLengthBytes;
        var tagLen = SquidVariableEncryption.V2TagLengthBytes;

        var salt = new byte[saltLen];
        var nonce = new byte[nonceLen];
        var tag = new byte[tagLen];
        var ctLen = envelope.Length - prefixLen - saltLen - nonceLen - tagLen;
        var ct = new byte[ctLen];

        Buffer.BlockCopy(envelope, prefixLen, salt, 0, saltLen);
        Buffer.BlockCopy(envelope, prefixLen + saltLen, nonce, 0, nonceLen);
        Buffer.BlockCopy(envelope, prefixLen + saltLen + nonceLen, ct, 0, ctLen);
        Buffer.BlockCopy(envelope, prefixLen + saltLen + nonceLen + ctLen, tag, 0, tagLen);

        var key = SquidVariableEncryption.DeriveV2Key(password, salt);

        var plain = new byte[ctLen];
        using var aes = new AesGcm(key, tagLen);
        aes.Decrypt(nonce, ct, tag, plain);

        return Encoding.UTF8.GetString(plain);
    }
}
