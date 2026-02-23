using System;
using System.IO;
using Squid.Tentacle.Certificate;

namespace Squid.UnitTests.Services.Tentacle;

public class TentacleCertificateManagerTests : IDisposable
{
    private readonly string _tempCertsPath;

    public TentacleCertificateManagerTests()
    {
        _tempCertsPath = Path.Combine(Path.GetTempPath(), $"squid-cert-test-{Guid.NewGuid():N}");
    }

    [Fact]
    public void LoadOrCreateCertificate_CreatesNewCert_WhenNoneExists()
    {
        var manager = new TentacleCertificateManager(_tempCertsPath);

        var cert = manager.LoadOrCreateCertificate();

        cert.ShouldNotBeNull();
        cert.Thumbprint.ShouldNotBeNullOrEmpty();
        cert.Subject.ShouldBe("CN=squid-tentacle");
        File.Exists(Path.Combine(_tempCertsPath, "tentacle-cert.pfx")).ShouldBeTrue();
    }

    [Fact]
    public void LoadOrCreateCertificate_LoadsExistingCert_WhenExists()
    {
        var manager = new TentacleCertificateManager(_tempCertsPath);

        var firstCert = manager.LoadOrCreateCertificate();
        var secondCert = manager.LoadOrCreateCertificate();

        secondCert.Thumbprint.ShouldBe(firstCert.Thumbprint);
    }

    [Fact]
    public void LoadOrCreateSubscriptionId_GeneratesNewId_WhenNoneExists()
    {
        var manager = new TentacleCertificateManager(_tempCertsPath);

        var id = manager.LoadOrCreateSubscriptionId();

        id.ShouldNotBeNullOrEmpty();
        id.Length.ShouldBe(32); // Guid without hyphens
        File.Exists(Path.Combine(_tempCertsPath, "subscription-id")).ShouldBeTrue();
    }

    [Fact]
    public void LoadOrCreateSubscriptionId_ReturnsSameId_OnSubsequentCalls()
    {
        var manager = new TentacleCertificateManager(_tempCertsPath);

        var firstId = manager.LoadOrCreateSubscriptionId();
        var secondId = manager.LoadOrCreateSubscriptionId();

        secondId.ShouldBe(firstId);
    }

    [Fact]
    public void LoadOrCreateSubscriptionId_PersistsAcrossInstances()
    {
        var manager1 = new TentacleCertificateManager(_tempCertsPath);
        var id1 = manager1.LoadOrCreateSubscriptionId();

        var manager2 = new TentacleCertificateManager(_tempCertsPath);
        var id2 = manager2.LoadOrCreateSubscriptionId();

        id2.ShouldBe(id1);
    }

    [Fact]
    public void LoadOrCreateCertificate_CreatesDirectory_WhenNotExists()
    {
        var deepPath = Path.Combine(_tempCertsPath, "nested", "certs");
        var manager = new TentacleCertificateManager(deepPath);

        var cert = manager.LoadOrCreateCertificate();

        cert.ShouldNotBeNull();
        Directory.Exists(deepPath).ShouldBeTrue();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempCertsPath))
                Directory.Delete(_tempCertsPath, recursive: true);
        }
        catch
        {
            // Best effort cleanup
        }
    }
}
