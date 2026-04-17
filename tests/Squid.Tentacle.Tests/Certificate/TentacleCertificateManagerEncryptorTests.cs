using Shouldly;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Security;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Certificate;

[Trait("Category", TentacleTestCategories.Core)]
public sealed class TentacleCertificateManagerEncryptorTests : IDisposable
{
    private readonly string _certsPath = Path.Combine(Path.GetTempPath(), $"squid-cert-enc-{Guid.NewGuid():N}");

    public TentacleCertificateManagerEncryptorTests() => Directory.CreateDirectory(_certsPath);

    public void Dispose()
    {
        try { if (Directory.Exists(_certsPath)) Directory.Delete(_certsPath, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void NewSubscriptionId_WithEncryptor_WrittenEncrypted()
    {
        var encryptor = new MachineIdKeyEncryptor("test-machine-id");
        var mgr = new TentacleCertificateManager(_certsPath, encryptor);

        var id = mgr.LoadOrCreateSubscriptionId();

        var path = Path.Combine(_certsPath, "subscription-id");
        var onDisk = File.ReadAllText(path);

        onDisk.ShouldNotBe(id, "written form must be the encrypted cipher, not plaintext");
        onDisk.ShouldStartWith("v1:");
        File.Exists(path + ".encrypted").ShouldBeTrue("marker file must exist so next load uses the encryptor path");

        // Re-open should decrypt back to the same id.
        var mgr2 = new TentacleCertificateManager(_certsPath, encryptor);
        mgr2.LoadOrCreateSubscriptionId().ShouldBe(id);
    }

    [Fact]
    public void LegacyPlaintextSubscriptionId_MigratedOnFirstEncryptorRead()
    {
        // Simulate pre-encryptor install: plaintext file, no marker.
        var legacyId = "legacy-plaintext-abc-123";
        File.WriteAllText(Path.Combine(_certsPath, "subscription-id"), legacyId);

        var encryptor = new MachineIdKeyEncryptor("test-machine-id");
        var mgr = new TentacleCertificateManager(_certsPath, encryptor);

        var id = mgr.LoadOrCreateSubscriptionId();

        id.ShouldBe(legacyId, "migration must preserve the existing id value");

        var path = Path.Combine(_certsPath, "subscription-id");
        File.ReadAllText(path).ShouldStartWith("v1:");
        File.Exists(path + ".encrypted").ShouldBeTrue();

        // Second load must decrypt successfully.
        var mgr2 = new TentacleCertificateManager(_certsPath, encryptor);
        mgr2.LoadOrCreateSubscriptionId().ShouldBe(legacyId);
    }

    [Fact]
    public void NoEncryptor_LegacyBehaviourPreserved()
    {
        var mgr = new TentacleCertificateManager(_certsPath, encryptor: null);

        var id = mgr.LoadOrCreateSubscriptionId();

        var onDisk = File.ReadAllText(Path.Combine(_certsPath, "subscription-id"));
        onDisk.ShouldBe(id, "without encryptor, on-disk form must be the raw id (back-compat)");
        File.Exists(Path.Combine(_certsPath, "subscription-id.encrypted")).ShouldBeFalse();
    }

    [Fact]
    public void NewCertificate_WithEncryptor_PasswordStoredEncrypted()
    {
        var encryptor = new MachineIdKeyEncryptor("test-machine-id");
        var mgr = new TentacleCertificateManager(_certsPath, encryptor);

        var cert = mgr.LoadOrCreateCertificate();

        cert.ShouldNotBeNull();
        var pwdPath = Path.Combine(_certsPath, "tentacle-cert.pfx.pwd");
        File.Exists(pwdPath).ShouldBeTrue("cert password file must be created");
        File.ReadAllText(pwdPath).ShouldStartWith("v1:");

        // Reopen — should load from the encrypted password file.
        var mgr2 = new TentacleCertificateManager(_certsPath, encryptor);
        var cert2 = mgr2.LoadOrCreateCertificate();
        cert2.Thumbprint.ShouldBe(cert.Thumbprint);
    }

    [Fact]
    public void WrongMachineId_CannotDecryptPreviouslyEncryptedId()
    {
        var encryptor1 = new MachineIdKeyEncryptor("machine-A");
        var mgr1 = new TentacleCertificateManager(_certsPath, encryptor1);
        mgr1.LoadOrCreateSubscriptionId();

        // Agent moved to a different host with different machine id.
        var encryptor2 = new MachineIdKeyEncryptor("machine-B");
        var mgr2 = new TentacleCertificateManager(_certsPath, encryptor2);

        var recovered = mgr2.LoadOrCreateSubscriptionId();

        // Graceful fallback: decryption fails, returns the raw cipher text rather
        // than blowing up. Operator sees warning + must re-register the agent.
        recovered.ShouldStartWith("v1:");
    }
}
