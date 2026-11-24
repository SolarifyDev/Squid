using System.Security.Cryptography.X509Certificates;
using Squid.Core.Setup.Secrets;

namespace Squid.UnitTests.Setup;

public sealed class InstallSecretsTests
{
    [Fact]
    public void GenerateMissing_EmptyConfiguration_GeneratesEverySecret()
    {
        using var dir = new TempDataDirectory();

        var secrets = InstallSecrets.GenerateMissing(dir.Configuration());

        secrets.Keys.ShouldBe(new[] { InstallSecrets.JwtKeyPath, InstallSecrets.MasterKeyPath, InstallSecrets.SelfCertBase64Path, InstallSecrets.SelfCertPasswordPath }, ignoreOrder: true);
        Convert.FromBase64String(secrets[InstallSecrets.MasterKeyPath]).Length.ShouldBe(32);
        Convert.FromBase64String(secrets[InstallSecrets.JwtKeyPath]).Length.ShouldBe(64);
    }

    [Fact]
    public void GenerateMissing_ExistingSecrets_AreNeverReplaced()
    {
        using var dir = new TempDataDirectory();
        var configuration = dir.Configuration(new() { [InstallSecrets.JwtKeyPath] = "jwt", [InstallSecrets.MasterKeyPath] = "mk", [InstallSecrets.SelfCertBase64Path] = "cert", [InstallSecrets.SelfCertPasswordPath] = "pw" });

        InstallSecrets.GenerateMissing(configuration).ShouldBeEmpty();
    }

    [Fact]
    public void GenerateMissing_TwoInstalls_GetDifferentSecrets()
    {
        using var dir = new TempDataDirectory();

        var first = InstallSecrets.GenerateMissing(dir.Configuration());
        var second = InstallSecrets.GenerateMissing(dir.Configuration());

        first[InstallSecrets.MasterKeyPath].ShouldNotBe(second[InstallSecrets.MasterKeyPath]);
        first[InstallSecrets.SelfCertBase64Path].ShouldNotBe(second[InstallSecrets.SelfCertBase64Path]);
    }

    [Fact]
    public void ServerCertificate_LoadsWithItsPassword_AndCarriesPrivateKey()
    {
        var generated = ServerCertificateFactory.Create();

        using var certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(generated.Base64), generated.Password);

        certificate.HasPrivateKey.ShouldBeTrue();
        certificate.Subject.ShouldBe(ServerCertificateFactory.SubjectName);
        certificate.Thumbprint.ShouldBe(generated.Thumbprint);
        certificate.NotAfter.ShouldBeGreaterThan(DateTime.UtcNow.AddYears(50));
    }

    [Fact]
    public void ServerCertificate_IsNeverTheRepositoryPublishedIdentity()
    {
        var generated = ServerCertificateFactory.Create();

        Squid.Core.Settings.SelfCert.SelfCertValidator.KnownPublishedThumbprints.ShouldNotContain(generated.Thumbprint);
    }
}
