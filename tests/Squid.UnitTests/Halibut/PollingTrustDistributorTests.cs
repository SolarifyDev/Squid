using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Halibut;
using Squid.Core.Halibut;
using Squid.Core.Services.Machines;

namespace Squid.UnitTests.Halibut;

public class PollingTrustDistributorTests : IDisposable
{
    private readonly Mock<IMachineDataProvider> _machineDataProvider = new();
    private readonly HalibutRuntime _halibutRuntime;
    private readonly IContainer _container;
    private readonly PollingTrustDistributor _distributor;

    public PollingTrustDistributorTests()
    {
        var (pfxBytes, password) = GenerateSelfSignedPfx();
        var cert = X509CertificateLoader.LoadPkcs12(pfxBytes, password);
        _halibutRuntime = new HalibutRuntimeBuilder().WithServerCertificate(cert).Build();

        var builder = new ContainerBuilder();
        builder.RegisterInstance(_halibutRuntime).As<HalibutRuntime>();
        builder.RegisterInstance(_machineDataProvider.Object).As<IMachineDataProvider>();
        _container = builder.Build();

        _distributor = new PollingTrustDistributor(_container);
    }

    public void Dispose()
    {
        _halibutRuntime?.Dispose();
        _container?.Dispose();
    }

    [Fact]
    public void Start_TrustsAllPollingMachines()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A", "THUMB-B", "THUMB-C" });

        _distributor.Start();

        _halibutRuntime.IsTrusted("THUMB-A").ShouldBeTrue();
        _halibutRuntime.IsTrusted("THUMB-B").ShouldBeTrue();
        _halibutRuntime.IsTrusted("THUMB-C").ShouldBeTrue();
    }

    [Fact]
    public void Reconfigure_ReplacesEntireTrustList()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A", "THUMB-B" });

        _distributor.Reconfigure();

        _halibutRuntime.IsTrusted("THUMB-A").ShouldBeTrue();
        _halibutRuntime.IsTrusted("THUMB-B").ShouldBeTrue();

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-B", "THUMB-C" });

        _distributor.Reconfigure();

        _halibutRuntime.IsTrusted("THUMB-A").ShouldBeFalse();
        _halibutRuntime.IsTrusted("THUMB-B").ShouldBeTrue();
        _halibutRuntime.IsTrusted("THUMB-C").ShouldBeTrue();
    }

    [Fact]
    public void Reconfigure_EmptyList_RemovesAllTrust()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A", "THUMB-B" });

        _distributor.Reconfigure();

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        _distributor.Reconfigure();

        _halibutRuntime.IsTrusted("THUMB-A").ShouldBeFalse();
        _halibutRuntime.IsTrusted("THUMB-B").ShouldBeFalse();
    }

    [Fact]
    public void ReconfigureIfMissing_KnownThumbprint_DoesNotReconfigure()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A" });

        _distributor.Reconfigure();
        _machineDataProvider.Invocations.Clear();

        _distributor.ReconfigureIfMissing("THUMB-A");

        _machineDataProvider.Verify(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void ReconfigureIfMissing_UnknownThumbprint_Reconfigures()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A" });

        _distributor.Reconfigure();

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A", "THUMB-B" });

        _distributor.ReconfigureIfMissing("THUMB-B");

        _halibutRuntime.IsTrusted("THUMB-B").ShouldBeTrue();
    }

    [Fact]
    public void Reconfigure_HalibutRuntimeUnavailable_DoesNotThrow()
    {
        var emptyBuilder = new ContainerBuilder();
        emptyBuilder.RegisterInstance(_machineDataProvider.Object).As<IMachineDataProvider>();
        using var emptyContainer = emptyBuilder.Build();

        var distributor = new PollingTrustDistributor(emptyContainer);

        Should.NotThrow(() => distributor.Reconfigure());
    }

    private static (byte[] pfxBytes, string password) GenerateSelfSignedPfx()
    {
        const string password = "test";
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));
        return (cert.Export(X509ContentType.Pfx, password), password);
    }
}
