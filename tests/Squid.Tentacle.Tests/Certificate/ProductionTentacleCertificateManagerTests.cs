using Shouldly;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Certificate;

/// <summary>
/// Pins the PRODUCTION wiring: <see cref="ProductionTentacleCertificateManager.Create"/> must
/// hand back a manager that encrypts the cert password (protecting the PFX private key) and the
/// subscription id at rest — using the REAL machine-key encryptor, real file IO, and a real
/// self-signed certificate. The encrypt/migrate logic itself is covered by
/// <see cref="TentacleCertificateManagerEncryptorTests"/>; these tests guard that production
/// actually goes through the encryptor-aware path instead of the plaintext bare constructor.
///
/// <para>Tier: 🟢 High-fidelity — real <c>MachineIdKeyEncryptor</c> (AES-256-GCM, machine-id KDF),
/// real temp-dir file IO, real X509 certificate generation.</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class ProductionTentacleCertificateManagerTests : IDisposable
{
    private readonly string _certsPath = Path.Combine(Path.GetTempPath(), $"squid-cert-prod-{Guid.NewGuid():N}");

    public ProductionTentacleCertificateManagerTests() => Directory.CreateDirectory(_certsPath);

    public void Dispose()
    {
        try { if (Directory.Exists(_certsPath)) Directory.Delete(_certsPath, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Create_NeverReturnsNull()
    {
        // Defensive contract: at-rest encryption is best-effort hardening, never a startup gate —
        // the factory must always produce a usable manager even if key derivation degrades.
        ProductionTentacleCertificateManager.Create(_certsPath).ShouldNotBeNull();
    }

    [Fact]
    public void Create_ProducesManagerThatEncryptsSubscriptionIdAtRest()
    {
        var manager = ProductionTentacleCertificateManager.Create(_certsPath);

        var id = manager.LoadOrCreateSubscriptionId();

        var idPath = Path.Combine(_certsPath, "subscription-id");
        var onDisk = File.ReadAllText(idPath);

        onDisk.ShouldNotBe(id, "the production factory must wire an encryptor — the on-disk form must be ciphertext, not the raw id");
        onDisk.ShouldStartWith("v1:"); // encrypted at-rest payloads carry the machine-key envelope version prefix
        File.Exists(idPath + ".encrypted").ShouldBeTrue("the encrypted marker must be written so the next load uses the encryptor path");
    }

    [Fact]
    public void Create_ProducesManagerThatEncryptsCertPasswordAtRest()
    {
        var manager = ProductionTentacleCertificateManager.Create(_certsPath);

        var cert = manager.LoadOrCreateCertificate();

        cert.ShouldNotBeNull();

        var pwdPath = Path.Combine(_certsPath, "tentacle-cert.pfx.pwd");
        File.Exists(pwdPath).ShouldBeTrue("an encryptor-aware manager records the random PFX password in a side file");
        File.ReadAllText(pwdPath).ShouldStartWith("v1:"); // the PFX password (which guards the private key) must be stored encrypted
    }

    [Fact]
    public void Create_EncryptionRoundTrips_AcrossReopen()
    {
        // Two managers built the same way (same host machine-id) must agree: the value written
        // encrypted by the first is decrypted back by the second. This is the contract the
        // register (write) → show-thumbprint / runtime (read) production paths depend on.
        var first = ProductionTentacleCertificateManager.Create(_certsPath);
        var id = first.LoadOrCreateSubscriptionId();
        var thumbprint = first.LoadOrCreateCertificate().Thumbprint;

        var second = ProductionTentacleCertificateManager.Create(_certsPath);

        second.LoadOrCreateSubscriptionId().ShouldBe(id, "a re-opened production manager must decrypt the subscription id back to the same value");
        second.LoadOrCreateCertificate().Thumbprint.ShouldBe(thumbprint, "a re-opened production manager must load the same cert (PFX password decrypted correctly)");
    }
}
