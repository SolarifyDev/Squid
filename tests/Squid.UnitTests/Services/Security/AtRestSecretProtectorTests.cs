using Squid.Core.Services.Security;

namespace Squid.UnitTests.Services.Security;

/// <summary>
/// Unit tests for <see cref="AtRestSecretProtector"/> — the SSOT value-transform extracted from the
/// account / feed / certificate DataProviders (and reused by the SSH proxy slice). Pins the two
/// non-breaking guarantees: idempotent Protect (null / empty / already-encrypted passes through, so
/// a re-save never double-wraps) and read-both Unprotect (delegates to the encryption service's
/// passthrough decrypt). The KDF scope is forwarded verbatim.
/// </summary>
public sealed class AtRestSecretProtectorTests
{
    private readonly Mock<IVariableEncryptionService> _encryption = new();
    private readonly AtRestSecretProtector _sut;

    public AtRestSecretProtectorTests()
    {
        _sut = new AtRestSecretProtector(_encryption.Object);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Protect_NullOrEmpty_ReturnsUnchanged_WithoutEncrypting(string value)
    {
        _sut.Protect(value, kdfScope: 0).ShouldBe(value);

        _encryption.Verify(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        _encryption.Verify(e => e.IsValidEncryptedValue(It.IsAny<string>()), Times.Never,
            failMessage: "null/empty must short-circuit before IsValidEncryptedValue is asked.");
    }

    [Fact]
    public void Protect_AlreadyEncrypted_ReturnsUnchanged_WithoutReencrypting()
    {
        const string envelope = "SQUID_ENCRYPTED_V2:abc";
        _encryption.Setup(e => e.IsValidEncryptedValue(envelope)).Returns(true);

        _sut.Protect(envelope, kdfScope: 7).ShouldBe(envelope);

        _encryption.Verify(e => e.EncryptAsync(It.IsAny<string>(), It.IsAny<int>()), Times.Never,
            failMessage: "an already-encrypted envelope must not be double-wrapped on re-save.");
    }

    [Fact]
    public void Protect_Plaintext_Encrypts_WithGivenScope()
    {
        _encryption.Setup(e => e.IsValidEncryptedValue("secret")).Returns(false);
        _encryption.Setup(e => e.EncryptAsync("secret", 7)).Returns("SQUID_ENCRYPTED_V2:xyz");

        _sut.Protect("secret", kdfScope: 7).ShouldBe("SQUID_ENCRYPTED_V2:xyz");

        _encryption.Verify(e => e.EncryptAsync("secret", 7), Times.Once,
            failMessage: "a plaintext value must be encrypted with the supplied KDF scope.");
    }

    [Fact]
    public async Task UnprotectAsync_DelegatesToDecrypt_WithGivenScope()
    {
        _encryption.Setup(e => e.DecryptAsync("SQUID_ENCRYPTED_V2:xyz", 7)).ReturnsAsync("secret");

        (await _sut.UnprotectAsync("SQUID_ENCRYPTED_V2:xyz", 7)).ShouldBe("secret");
    }

    [Fact]
    public async Task UnprotectAsync_LegacyPlaintext_PassesThrough_ViaReadBothDecrypt()
    {
        // Read-both is DecryptAsync's contract — the protector just delegates. Pin that a value the
        // encryption service returns verbatim (legacy unprefixed plaintext) flows through unchanged.
        _encryption.Setup(e => e.DecryptAsync("legacy-plaintext", 0)).ReturnsAsync("legacy-plaintext");

        (await _sut.UnprotectAsync("legacy-plaintext", 0)).ShouldBe("legacy-plaintext");
    }
}
