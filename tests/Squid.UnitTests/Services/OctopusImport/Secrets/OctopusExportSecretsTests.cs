using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;

namespace Squid.UnitTests.Services.OctopusImport.Secrets;

public class OctopusExportSecretsTests
{
    private const string Password = "export-password";

    [Fact]
    public void CipherConstants_ArePinnedToOctopusWireFormat()
    {
        // Changing any of these makes every existing Octopus export undecryptable.
        OctopusExportCipher.KeySizeBytes.ShouldBe(16);
        OctopusExportCipher.IvSizeBytes.ShouldBe(16);
        OctopusExportCipher.Pbkdf2Iterations.ShouldBe(1_000);
        OctopusExportCipher.PasswordSalt.ShouldBe("Octopuss");
    }

    [Theory]
    [InlineData("s3cr3t")]
    [InlineData("")]
    [InlineData("multi\nline 'quoted' \"value\" ünïcødé 密码")]
    public void TryDecrypt_ValueEncryptedWithSamePassword_ReturnsPlaintext(string plaintext)
    {
        var secrets = OctopusExportSecrets.FromPassword(Password);

        secrets.TryDecrypt(OctopusExportTestCipher.Encrypt(plaintext, Password), out var decrypted).ShouldBeTrue();
        decrypted.ShouldBe(plaintext);
        secrets.CanDecrypt(OctopusExportTestCipher.Encrypt(plaintext, Password)).ShouldBeTrue();
    }

    [Fact]
    public void TryDecrypt_ValueEncryptedWithDifferentPassword_Fails()
    {
        var encrypted = OctopusExportTestCipher.Encrypt("value-long-enough-to-span-blocks-0123456789", "other-password");
        var secrets = OctopusExportSecrets.FromPassword(Password);

        secrets.TryDecrypt(encrypted, out var decrypted).ShouldBeFalse();
        decrypted.ShouldBeNull();
        secrets.CanDecrypt(encrypted).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("********")]
    [InlineData("not-encrypted")]
    [InlineData("abc|def|ghi")]
    [InlineData("|AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("!!!|AAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAA==|AAAA")]
    [InlineData("AAAA|AAAAAAAAAAAAAAAAAAAAAA==")]
    public void TryDecrypt_MalformedOrMaskedValue_Fails(string value)
    {
        OctopusExportSecrets.FromPassword(Password).TryDecrypt(value, out var decrypted).ShouldBeFalse();
        decrypted.ShouldBeNull();
    }

    [Fact]
    public void TryDecrypt_PlaintextThatIsNotUtf8_Fails()
    {
        var encrypted = OctopusExportTestCipher.EncryptRawBytes([0xC3, 0x28, 0xFF, 0xFE], Password);

        OctopusExportSecrets.FromPassword(Password).TryDecrypt(encrypted, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromPassword_Empty_ReturnsNone(string password)
    {
        var secrets = OctopusExportSecrets.FromPassword(password);

        secrets.ShouldBeSameAs(OctopusExportSecrets.None);
        secrets.IsAvailable.ShouldBeFalse();
        secrets.TryDecrypt(OctopusExportTestCipher.Encrypt("x", "anything"), out _).ShouldBeFalse();
    }

    [Fact]
    public void ToString_NeverExposesKeyMaterial()
    {
        OctopusExportSecrets.FromPassword(Password).ToString().ShouldNotContain(Password);
        OctopusExportSecrets.FromPassword(Password).ToString().ShouldBe("OctopusExportSecrets(available)");
    }

    [Fact]
    public void SensitiveValues_ResolvesOnlyWhenSecretsDecryptTheValue()
    {
        var secrets = OctopusExportSecrets.FromPassword(Password);
        var encrypted = new OctopusVariableDto { Name = "Db:Password", Type = "Sensitive", Value = OctopusExportTestCipher.Encrypt("p@ss", Password) };
        var empty = new OctopusVariableDto { Name = "Empty", Type = "Sensitive", Value = null };
        var masked = new OctopusVariableDto { Name = "Masked", Type = "Sensitive", Value = "********" };

        OctopusImportSensitiveValues.TryResolve(encrypted, secrets, out var value).ShouldBeTrue();
        value.ShouldBe("p@ss");
        OctopusImportSensitiveValues.TryResolve(empty, secrets, out var emptyValue).ShouldBeTrue();
        emptyValue.ShouldBe(string.Empty);
        OctopusImportSensitiveValues.IsResolvable(masked, secrets).ShouldBeFalse();
        OctopusImportSensitiveValues.IsResolvable(encrypted, OctopusExportSecrets.None).ShouldBeFalse();
        OctopusImportSensitiveValues.IsResolvable(empty, OctopusExportSecrets.None).ShouldBeFalse();
        OctopusImportSensitiveValues.IsResolvable(null, secrets).ShouldBeFalse();
    }

    [Fact]
    public void FeedCredentials_ResolvedOnlyAsACompletePair()
    {
        var secrets = OctopusExportSecrets.FromPassword(Password);
        var withPassword = new OctopusFeedDto { Username = "robot", Password = OctopusExportTestCipher.Encrypt("registry-token", Password) };

        OctopusImportFeedCredentials.Resolve(withPassword, secrets).ShouldBe(new OctopusImportFeedCredentials("robot", "registry-token", true));
        OctopusImportFeedCredentials.Resolve(new OctopusFeedDto { Username = "anon" }, OctopusExportSecrets.None).ShouldBe(new OctopusImportFeedCredentials("anon", null, true));
        OctopusImportFeedCredentials.Resolve(new OctopusFeedDto(), OctopusExportSecrets.None).ShouldBe(new OctopusImportFeedCredentials(null, null, true));
        OctopusImportFeedCredentials.Resolve(withPassword, OctopusExportSecrets.None).ShouldBe(OctopusImportFeedCredentials.Unresolved);
        OctopusImportFeedCredentials.Resolve(new OctopusFeedDto { Username = "robot", Password = "********" }, secrets).ShouldBe(OctopusImportFeedCredentials.Unresolved);
        OctopusImportFeedCredentials.IsResolvable(withPassword, secrets).ShouldBeTrue();
        OctopusImportFeedCredentials.IsResolvable(withPassword, OctopusExportSecrets.None).ShouldBeFalse();
        OctopusImportFeedCredentials.Resolve(withPassword, secrets).ToString().ShouldNotContain("registry-token");
    }

    [Fact]
    public void Accessor_DefaultsToNone_AndNullResetsToNone()
    {
        var accessor = new OctopusExportSecretsAccessor();

        accessor.Secrets.ShouldBeSameAs(OctopusExportSecrets.None);

        accessor.Secrets = OctopusExportSecrets.FromPassword(Password);
        accessor.Secrets.IsAvailable.ShouldBeTrue();

        accessor.Secrets = null;
        accessor.Secrets.ShouldBeSameAs(OctopusExportSecrets.None);
    }
}
