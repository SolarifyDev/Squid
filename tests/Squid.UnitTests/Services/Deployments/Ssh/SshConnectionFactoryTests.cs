using System.Linq;
using Squid.Core.Services.DeploymentExecution.Ssh;

namespace Squid.UnitTests.Services.Deployments.Ssh;

public class SshConnectionFactoryTests
{
    private readonly SshConnectionFactory _factory = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void CreateScope_EmptyHost_ThrowsArgumentException(string host)
    {
        var info = new SshConnectionInfo(host, 22, "user", null, null, "pass", null, TimeSpan.FromSeconds(10));

        Should.Throw<ArgumentException>(() => _factory.CreateScope(info));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void CreateScope_EmptyUsername_ThrowsArgumentException(string username)
    {
        var info = new SshConnectionInfo("host", 22, username, null, null, "pass", null, TimeSpan.FromSeconds(10));

        Should.Throw<ArgumentException>(() => _factory.CreateScope(info));
    }

    [Fact]
    public void CreateScope_NoAuthMethod_ThrowsInvalidOperationException()
    {
        var info = new SshConnectionInfo("host", 22, "user", null, null, null, null, TimeSpan.FromSeconds(10));

        Should.Throw<InvalidOperationException>(() => _factory.CreateScope(info));
    }

    [Fact]
    public void CreateScope_NoAuthMethod_EmptyStrings_ThrowsInvalidOperationException()
    {
        var info = new SshConnectionInfo("host", 22, "user", "", "", "", null, TimeSpan.FromSeconds(10));

        Should.Throw<InvalidOperationException>(() => _factory.CreateScope(info));
    }

    [Fact]
    public void CreateScope_WithPassword_ReturnsScope()
    {
        var info = new SshConnectionInfo("host", 22, "user", null, null, "pass123", null, TimeSpan.FromSeconds(10));

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.ShouldBeOfType<SshConnectionScope>();
        scope.Dispose();
    }

    [Fact]
    public void CreateScope_WithPrivateKey_ReturnsScope()
    {
        var key = GenerateTestRsaPrivateKey();
        var info = new SshConnectionInfo("host", 22, "user", key, null, null, null, TimeSpan.FromSeconds(10));

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.Dispose();
    }

    [Fact]
    public void CreateScope_WithPrivateKeyAndPassphrase_ReturnsScope()
    {
        var key = GenerateTestRsaPrivateKeyWithPassphrase("test123");
        var info = new SshConnectionInfo("host", 22, "user", key, "test123", null, null, TimeSpan.FromSeconds(10));

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.Dispose();
    }

    [Fact]
    public void CreateScope_WithBothKeyAndPassword_ReturnsScope()
    {
        var key = GenerateTestRsaPrivateKey();
        var info = new SshConnectionInfo("host", 22, "user", key, null, "pass", null, TimeSpan.FromSeconds(10));

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.Dispose();
    }

    [Fact]
    public void CreateScope_ZeroTimeout_UsesDefaultTimeout()
    {
        var info = new SshConnectionInfo("host", 22, "user", null, null, "pass", null, TimeSpan.Zero);

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.Dispose();
    }

    [Fact]
    public void CreateScope_WithFingerprint_ReturnsScope()
    {
        var info = new SshConnectionInfo("host", 22, "user", null, null, "pass", "SHA256:abc123", TimeSpan.FromSeconds(10));

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.Dispose();
    }

    // ========================================================================
    // NormalizePem
    // ========================================================================

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NormalizePem_NullOrWhitespace_ReturnsAsIs(string input)
    {
        SshConnectionFactory.NormalizePem(input).ShouldBe(input);
    }

    [Fact]
    public void NormalizePem_NoPemMarkers_ReturnsAsIs()
    {
        var input = "not a pem key at all";

        SshConnectionFactory.NormalizePem(input).ShouldBe(input);
    }

    [Fact]
    public void NormalizePem_AlreadyFormatted_PreservesBase64()
    {
        var key = GenerateTestRsaPrivateKey();

        var normalized = SshConnectionFactory.NormalizePem(key);

        ExtractBase64Body(normalized).ShouldBe(ExtractBase64Body(key));
    }

    [Fact]
    public void NormalizePem_SpacesInsteadOfNewlines_RestoresValidPem()
    {
        var key = GenerateTestRsaPrivateKey();
        var mangled = key.Replace("\n", "   ");

        var normalized = SshConnectionFactory.NormalizePem(mangled);

        normalized.ShouldStartWith("-----BEGIN");
        normalized.ShouldContain("\n");
        ExtractBase64Body(normalized).ShouldBe(ExtractBase64Body(key));
    }

    [Fact]
    public void NormalizePem_SpacesInsteadOfNewlines_ProducesValidPrivateKey()
    {
        var key = GenerateTestRsaPrivateKey();
        var mangled = key.Replace("\n", "   ");

        var normalized = SshConnectionFactory.NormalizePem(mangled);
        var info = new SshConnectionInfo("host", 22, "user", normalized, null, null, null, TimeSpan.FromSeconds(10));

        var scope = _factory.CreateScope(info);

        scope.ShouldNotBeNull();
        scope.Dispose();
    }

    [Fact]
    public void NormalizePem_MixedWhitespace_RestoresValidPem()
    {
        var key = GenerateTestRsaPrivateKey();
        var mangled = key.Replace("\n", " \t ");

        var normalized = SshConnectionFactory.NormalizePem(mangled);

        ExtractBase64Body(normalized).ShouldBe(ExtractBase64Body(key));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static string ExtractBase64Body(string pem)
    {
        var lines = pem.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(lines.Where(l => !l.StartsWith("-----")).Select(l => l.Trim()));
    }

    private static string GenerateTestRsaPrivateKey()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportRSAPrivateKeyPem();
        return pem;
    }

    private static string GenerateTestRsaPrivateKeyWithPassphrase(string passphrase)
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase.AsSpan(), new System.Security.Cryptography.PbeParameters(System.Security.Cryptography.PbeEncryptionAlgorithm.Aes256Cbc, System.Security.Cryptography.HashAlgorithmName.SHA256, 100000));
        return pem;
    }
}
