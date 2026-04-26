using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Security;
using Xunit;

namespace Squid.UnitTests.Services.Security;

/// <summary>
/// P1-B.10 (Phase-8): pin the V2 envelope upgrade for variable encryption.
///
/// <para><b>The bug it closes</b>: pre-fix <c>DeriveKey</c> used a
/// DETERMINISTIC salt (variableSetId padded with zeros) and only
/// <c>10_000</c> PBKDF2 iterations — well below the OWASP 2023
/// recommendation of 600k. Two consequences:</para>
/// <list type="bullet">
///   <item>Same salt across all variables in a set → if an attacker
///         recovers the derived key for ONE variable they recover ALL
///         of them.</item>
///   <item>10k iters is brute-force-feasible on modern hardware; OWASP
///         600k is the modern floor.</item>
/// </list>
///
/// <para><b>Fix</b>: V2 envelope with prefix <c>SQUID_ENCRYPTED_V2:</c>,
/// random per-payload 16-byte salt embedded in the envelope, and 600k
/// PBKDF2-SHA256 iters. Existing V1 ciphertexts (<c>SQUID_ENCRYPTED:</c>
/// prefix) remain readable via the dual-format decrypt path — no DB
/// migration needed; ciphertexts upgrade naturally as variables are
/// rewritten.</para>
/// </summary>
public sealed class VariableEncryptionServiceB10Tests
{
    [Fact]
    public void EncryptionPrefixV2_PinnedLiteral()
    {
        // Operators may grep ciphertexts in DB dumps to identify
        // pre/post-V2 entries. Pin the literal.
        VariableEncryptionService.EncryptionPrefixV2.ShouldBe("SQUID_ENCRYPTED_V2:");
    }

    [Fact]
    public void Pbkdf2IterationsV2_AtOrAboveOwasp2023Floor()
    {
        VariableEncryptionService.Pbkdf2IterationsV2.ShouldBeGreaterThanOrEqualTo(600_000,
            customMessage: "OWASP 2023 PBKDF2-SHA256 recommendation; lowering past this requires explicit re-evaluation.");
    }

    [Fact]
    public void SaltSizeBytesV2_Is16Bytes()
    {
        VariableEncryptionService.SaltSizeBytesV2.ShouldBe(16);
    }

    [Theory]
    [InlineData("simple-secret")]
    [InlineData("")]   // empty roundtrip should also be safe
    [InlineData("multi-line\nsecret\nwith\nnewlines")]
    [InlineData("unicode: 你好 🔐 deployment")]
    [InlineData("80-char-payload-hits-multiple-aes-blocks-0123456789ABCDEF0123456789ABCDEF")]
    public async Task EncryptThenDecrypt_V2_RoundtripsExactly(string plaintext)
    {
        var service = MakeService();

        var encrypted = service.EncryptAsync(plaintext, variableSetId: 42);

        if (string.IsNullOrEmpty(plaintext))
        {
            // Service short-circuits on empty input — returns plaintext unchanged.
            encrypted.ShouldBe(plaintext);
            return;
        }

        encrypted.ShouldStartWith(VariableEncryptionService.EncryptionPrefixV2,
            customMessage: "Phase-8 encrypts MUST emit V2; legacy V1 prefix is read-only.");

        var decrypted = await service.DecryptAsync(encrypted, variableSetId: 42);

        decrypted.ShouldBe(plaintext);
    }

    [Fact]
    public void EncryptTwice_SamePlaintext_ProducesDifferentCiphertexts()
    {
        // Random per-payload salt + random nonce → identical plaintext
        // produces DIFFERENT envelope bytes each call. Critical for the
        // "attacker can detect repeated values in DB" attack.
        var service = MakeService();

        var c1 = service.EncryptAsync("same-secret", variableSetId: 7);
        var c2 = service.EncryptAsync("same-secret", variableSetId: 7);

        c1.ShouldNotBe(c2,
            customMessage:
                "Two encrypts of the same plaintext must NEVER produce the same ciphertext. " +
                "Pre-fix V1 used deterministic salt — same plaintext + same VariableSetId → " +
                "identical bytes. Post-fix V2 uses random per-payload salt + random nonce.");
    }

    [Fact]
    public async Task DecryptV1Ciphertext_PostPhase8Service_StillReadable()
    {
        // Backward compat: a ciphertext written by a pre-Phase-8 server
        // (V1 prefix `SQUID_ENCRYPTED:`) MUST decrypt cleanly post-upgrade.
        // Otherwise every existing sensitive variable in production would
        // become unreadable on deploy.
        //
        // We construct a V1 envelope by hand using the same KDF / cipher
        // params the pre-fix code used. If post-fix DecryptAsync still
        // returns the original plaintext, the dual-format path works.
        var service = MakeService();

        var v1Envelope = ConstructV1Envelope("legacy-payload", variableSetId: 100, masterKey: GetTestMasterKey());

        var decrypted = await service.DecryptAsync(v1Envelope, variableSetId: 100);

        decrypted.ShouldBe("legacy-payload",
            customMessage: "V1 ciphertexts in DB MUST remain readable post-upgrade.");
    }

    [Fact]
    public void IsValidEncryptedValue_RecognisesBothV1AndV2()
    {
        var service = MakeService();

        var v2 = service.EncryptAsync("test", variableSetId: 1);
        var v1 = ConstructV1Envelope("test", variableSetId: 1, masterKey: GetTestMasterKey());

        service.IsValidEncryptedValue(v2).ShouldBeTrue();
        service.IsValidEncryptedValue(v1).ShouldBeTrue();
        service.IsValidEncryptedValue("plaintext-not-encrypted").ShouldBeFalse();
        service.IsValidEncryptedValue("").ShouldBeFalse();
        service.IsValidEncryptedValue(null).ShouldBeFalse();
    }

    [Fact]
    public void V2EnvelopeLayout_PrefixSaltNonceTagCiphertext()
    {
        // Pin the byte layout — any future shape change (e.g. moving salt
        // after nonce) would break dual-format decrypt and break in-flight
        // ciphertexts. Layout: V2_prefix || base64(salt(16) || nonce(12)
        // || tag(16) || ciphertext(var)).
        var service = MakeService();
        const string plaintext = "layout-pin-test";

        var encrypted = service.EncryptAsync(plaintext, variableSetId: 1);
        var base64Body = encrypted.Substring(VariableEncryptionService.EncryptionPrefixV2.Length);
        var envelope = Convert.FromBase64String(base64Body);

        // Salt(16) + Nonce(12) + Tag(16) + Ciphertext(plaintext.Length, since AES-GCM doesn't pad)
        var expectedLength = 16 + 12 + 16 + System.Text.Encoding.UTF8.GetByteCount(plaintext);
        envelope.Length.ShouldBe(expectedLength,
            customMessage: $"Envelope must be exactly salt(16) + nonce(12) + tag(16) + ciphertext({System.Text.Encoding.UTF8.GetByteCount(plaintext)}) = {expectedLength}.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VariableEncryptionService MakeService()
    {
        var key = GetTestMasterKey();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Security:VariableEncryption:MasterKey"] = Convert.ToBase64String(key)
            })
            .Build();
        var setting = new SecuritySetting(configuration);
        return new VariableEncryptionService(setting);
    }

    private static byte[] GetTestMasterKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(i + 1);
        return key;
    }

    /// <summary>
    /// Reconstructs a V1 ciphertext using the SAME KDF/cipher parameters
    /// the pre-Phase-8 service used. Lets us prove the dual-format
    /// decrypt path can read pre-upgrade ciphertexts without spinning up
    /// a separate "old-version" service.
    /// </summary>
    private static string ConstructV1Envelope(string plaintext, int variableSetId, byte[] masterKey)
    {
        // V1 KDF: deterministic salt from variableSetId padded to 16 bytes,
        // 10_000 iters, SHA-256, 32-byte output.
        var salt = BitConverter.GetBytes(variableSetId);
        Array.Resize(ref salt, 16);
        using var pbkdf2 = new System.Security.Cryptography.Rfc2898DeriveBytes(
            masterKey, salt, 10000, System.Security.Cryptography.HashAlgorithmName.SHA256);
        var key = pbkdf2.GetBytes(32);

        // V1 cipher: AES-GCM, 12-byte random nonce, 16-byte tag.
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(12);
        var plainBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var aes = new System.Security.Cryptography.AesGcm(key, 16);
        aes.Encrypt(nonce, plainBytes, ciphertext, tag);

        // V1 layout: nonce(12) || tag(16) || ciphertext(var)  → no salt,
        // no version byte, base64-encoded.
        var envelope = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, envelope, 0, 12);
        Buffer.BlockCopy(tag, 0, envelope, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, envelope, 28, ciphertext.Length);

        return $"SQUID_ENCRYPTED:{Convert.ToBase64String(envelope)}";
    }
}
