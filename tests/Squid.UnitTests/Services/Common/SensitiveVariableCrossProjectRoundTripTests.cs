using System.Text;
using Squid.Calamari.Variables;
using Squid.Core.Services.Common;
using Squid.Message.Hardening;
using CalamariMode = Squid.Calamari.Hardening.EnforcementMode;

namespace Squid.UnitTests.Services.Common;

/// <summary>
/// P0-B.3 cross-project round-trip guard (2026-04-24 audit).
///
/// <para>Each side's own test file uses hand-mirrored helpers (server tests have a
/// <c>DecryptV2Manually</c>; Calamari tests have an <c>EncryptV2Manually</c>) to
/// avoid a cross-project dep. That protects against spec drift on either side
/// individually — but if BOTH hand-mirrors are wrong the same way, neither test
/// fails and the real pipeline silently breaks at first deploy.</para>
///
/// <para>This file wires the two REAL classes together: server's
/// <c>SquidVariableEncryption.Encrypt</c> output is fed straight into Calamari's
/// <c>SensitiveVariableDecryptor.Decrypt</c>. If the envelope layout, KDF params,
/// or prefix literal ever drift apart, the round-trip assertion fails loudly
/// here instead of at deploy time.</para>
///
/// <para>Added in Phase-4 audit follow-up after the initial commit shipped only
/// hand-mirror tests.</para>
/// </summary>
public sealed class SensitiveVariableCrossProjectRoundTripTests
{
    private const string SharedPassword = "cross-project-audit-password";

    [Theory]
    [InlineData("simple-secret")]
    [InlineData("")]
    [InlineData("key1=value1\nkey2=value2\nkey3=val🔐ue3")]   // multiline + unicode
    [InlineData("{\"apiKey\":\"sk-abc123\",\"dbUrl\":\"postgres://user:pass@host:5432/db\"}")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]  // 80-char payload hits multiple AES blocks
    public void V2_RealServerEncrypt_RealCalamariDecrypt_Roundtrips(string plaintext)
    {
        // Server: real SquidVariableEncryption producing V2 envelope.
        var encryptor = new SquidVariableEncryption(SharedPassword);
        var envelope = encryptor.Encrypt(plaintext, EnforcementMode.Strict);

        // Calamari: real SensitiveVariableDecryptor reading the envelope.
        var decryptor = new SensitiveVariableDecryptor(SharedPassword);
        var recovered = decryptor.Decrypt(envelope, CalamariMode.Warn);

        recovered.ShouldBe(plaintext,
            customMessage:
                "V2 round-trip must recover the original plaintext EXACTLY when both sides use their " +
                "real implementations. A mismatch here means the envelope layout, KDF params, " +
                "nonce/salt framing, or prefix literal drifted between the two sides silently — " +
                "deploys would fail at first sensitive-variable script and the hand-mirrored tests " +
                "on either side wouldn't catch it.");
    }

    [Theory]
    [InlineData("legacy-payload")]
    [InlineData("")]
    [InlineData("multi\nline\nwith = signs = everywhere")]
    public void V1_RealServerEncrypt_RealCalamariDecrypt_Roundtrips(string plaintext)
    {
        // Server in Warn mode (default) emits V1 for rolling-upgrade compat.
        // Calamari must still decrypt it — the whole point of V1 dual-path reader.
        var encryptor = new SquidVariableEncryption(SharedPassword);
        var envelope = encryptor.Encrypt(plaintext, EnforcementMode.Warn);

        var decryptor = new SensitiveVariableDecryptor(SharedPassword);
        var recovered = decryptor.Decrypt(envelope, CalamariMode.Warn);

        recovered.ShouldBe(plaintext);
    }

    [Fact]
    public void V2_RealRoundTrip_WrongPassword_Throws()
    {
        // The GCM auth tag must fail verification if the password is wrong —
        // proves the real KDF + real GCM path detect the mismatch, not just the
        // hand-mirror helpers.
        var encryptor = new SquidVariableEncryption(SharedPassword);
        var envelope = encryptor.Encrypt("secret", EnforcementMode.Strict);

        var decryptor = new SensitiveVariableDecryptor("WRONG-PASSWORD");

        Should.Throw<System.Security.Cryptography.CryptographicException>(
            () => decryptor.Decrypt(envelope, CalamariMode.Warn),
            customMessage: "real V2 decrypt with wrong password must fail at the AES-GCM auth check");
    }

    [Fact]
    public void V2_RealRoundTrip_TamperedCiphertext_Throws()
    {
        var encryptor = new SquidVariableEncryption(SharedPassword);
        var envelope = encryptor.Encrypt("secret", EnforcementMode.Strict);

        // Flip a byte in the ciphertext region (past V2__(4) + salt(16) + nonce(12) = 32).
        envelope[32] ^= 0xFF;

        var decryptor = new SensitiveVariableDecryptor(SharedPassword);

        Should.Throw<System.Security.Cryptography.CryptographicException>(
            () => decryptor.Decrypt(envelope, CalamariMode.Warn),
            customMessage:
                "AES-GCM tag must catch ciphertext tampering on the REAL round-trip path. " +
                "This is the whole reason for the V2 upgrade over V1-CBC-no-MAC.");
    }

    [Fact]
    public void V2_TwoEncrypts_ProduceDifferentEnvelopes()
    {
        // Sanity check on the real path: same plaintext + same password must not
        // produce the same bytes twice (random salt + random nonce per call).
        var encryptor = new SquidVariableEncryption(SharedPassword);

        var a = encryptor.Encrypt("same-plaintext", EnforcementMode.Strict);
        var b = encryptor.Encrypt("same-plaintext", EnforcementMode.Strict);

        Convert.ToBase64String(a).ShouldNotBe(Convert.ToBase64String(b));

        // Both must still decrypt to the same plaintext.
        var decryptor = new SensitiveVariableDecryptor(SharedPassword);
        decryptor.Decrypt(a, CalamariMode.Warn).ShouldBe("same-plaintext");
        decryptor.Decrypt(b, CalamariMode.Warn).ShouldBe("same-plaintext");
    }

    [Fact]
    public void MixedFleet_ServerEmitsV1_CalamariStrictMode_Rejects()
    {
        // Operator scenario: fleet partially upgraded. Server (still on older
        // behaviour or operator hasn't flipped to strict yet) emits V1. Calamari
        // is configured strict after operator confirmed fleet was upgraded —
        // but this agent somehow still got a V1 payload. Must reject loudly so
        // operator sees the misconfiguration rather than silently decrypting
        // weak legacy crypto.
        var encryptor = new SquidVariableEncryption(SharedPassword);
        var envelope = encryptor.Encrypt("legacy-payload", EnforcementMode.Warn);  // V1

        var decryptor = new SensitiveVariableDecryptor(SharedPassword);

        var ex = Should.Throw<InvalidOperationException>(
            () => decryptor.Decrypt(envelope, CalamariMode.Strict));

        ex.Message.ShouldContain("LEGACY V1",
            customMessage: "error must name the legacy format so operator knows why it was rejected");
    }

    [Fact]
    public void V2EnvelopeByteLayout_MatchesCrossSideExpectation()
    {
        // Pin the exact byte layout both sides agree on: V2__(4) + salt(16) + nonce(12) +
        // ct(var) + tag(16). A subtle swap (e.g. nonce-before-salt) would break cross-side
        // decrypt while both sides' hand-mirror tests keep passing.
        var encryptor = new SquidVariableEncryption(SharedPassword);
        const string plaintext = "layout-sanity-check";
        var envelope = encryptor.Encrypt(plaintext, EnforcementMode.Strict);

        // Prefix bytes
        Encoding.ASCII.GetString(envelope, 0, 4).ShouldBe("V2__");

        // Total length check: prefix(4) + salt(16) + nonce(12) + ct(len(plaintext)) + tag(16)
        var expectedLength = 4 + 16 + 12 + Encoding.UTF8.GetByteCount(plaintext) + 16;
        envelope.Length.ShouldBe(expectedLength);

        // And the real Calamari decryptor interprets this layout identically.
        var decryptor = new SensitiveVariableDecryptor(SharedPassword);
        decryptor.Decrypt(envelope, CalamariMode.Warn).ShouldBe(plaintext);
    }
}
