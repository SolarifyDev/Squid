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
    public async Task Start_TrustsAllPollingMachines()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "THUMB-A", "THUMB-B", "THUMB-C" });

        _distributor.Start();
        await WaitForInitialLoadAsync();

        _halibutRuntime.IsTrusted("THUMB-A").ShouldBeTrue();
        _halibutRuntime.IsTrusted("THUMB-B").ShouldBeTrue();
        _halibutRuntime.IsTrusted("THUMB-C").ShouldBeTrue();
    }

    [Fact]
    public async Task Start_DbSlow_DoesNotBlock_InitialLoadCompletedFlipsLater()
    {
        var release = new TaskCompletionSource();
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                await release.Task;
                return new List<string> { "SLOW-DB" };
            });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        _distributor.Start();
        sw.Stop();

        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromMilliseconds(250), "Start() must not block on the DB call");
        _distributor.InitialLoadCompleted.ShouldBeFalse();

        release.SetResult();
        await WaitForInitialLoadAsync();

        _distributor.InitialLoadCompleted.ShouldBeTrue();
        _halibutRuntime.IsTrusted("SLOW-DB").ShouldBeTrue();
    }

    private async Task WaitForInitialLoadAsync()
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (!_distributor.InitialLoadCompleted)
        {
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("InitialLoadCompleted did not flip within 5s");
            await Task.Delay(25);
        }
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

    // ── P1 (Phase-6, post-Phase-5 deep audit) ────────────────────────────────
    //
    // Context: ReconfigureIfMissing was called from MachineRegistrationService
    // .TentacleListening.cs:50 INSIDE an async controller call, transitively
    // executing GetPollingThumbprintsAsync().GetAwaiter().GetResult() on a
    // Kestrel request thread. Burst registration → request-thread starvation.
    //
    // Fix shape: add ReconfigureAsync / ReconfigureIfMissingAsync. Sync
    // counterparts kept for backward compat (StartAsync delegates to async).
    // Pinned by these tests + reverse-verify.

    [Fact]
    public async Task ReconfigureAsync_AwaitsAsyncProvider_NoSyncOverAsync()
    {
        // Pin: the async method must reach the async data provider via await,
        // not via .GetAwaiter().GetResult(). Test by holding the provider's
        // task open and asserting ReconfigureAsync() returns a non-completed
        // Task — a sync-over-async implementation would block the test thread
        // forever (or deadlock on a sync context).
        var release = new TaskCompletionSource();
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                await release.Task;
                return new List<string> { "ASYNC-AWAIT" };
            });

        var task = _distributor.ReconfigureAsync();

        task.IsCompleted.ShouldBeFalse(
            customMessage: "ReconfigureAsync must await the provider — sync-over-async would block here.");

        release.SetResult();
        await task;

        _halibutRuntime.IsTrusted("ASYNC-AWAIT").ShouldBeTrue();
    }

    [Fact]
    public async Task ReconfigureIfMissingAsync_UnknownThumbprint_TriggersAsyncReconfigure()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "EXISTING" });

        await _distributor.ReconfigureAsync();

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "EXISTING", "NEW-THUMB" });

        await _distributor.ReconfigureIfMissingAsync("NEW-THUMB");

        _halibutRuntime.IsTrusted("NEW-THUMB").ShouldBeTrue();
    }

    [Fact]
    public async Task ReconfigureIfMissingAsync_AlreadyTrusted_NoDbCall()
    {
        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "KNOWN" });

        await _distributor.ReconfigureAsync();
        _machineDataProvider.Invocations.Clear();

        await _distributor.ReconfigureIfMissingAsync("KNOWN");

        _machineDataProvider.Verify(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()), Times.Never,
            failMessage: "Already-trusted short-circuit must skip the DB hit on the async path too.");
    }

    [Fact]
    public async Task ReconfigureIfMissingAsync_NullOrEmptyThumbprint_NoOp()
    {
        await _distributor.ReconfigureIfMissingAsync(null);
        await _distributor.ReconfigureIfMissingAsync("");

        _machineDataProvider.Verify(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReconfigureAsync_HalibutRuntimeUnavailable_DoesNotThrow()
    {
        var emptyBuilder = new ContainerBuilder();
        emptyBuilder.RegisterInstance(_machineDataProvider.Object).As<IMachineDataProvider>();
        using var emptyContainer = emptyBuilder.Build();

        var distributor = new PollingTrustDistributor(emptyContainer);

        await Should.NotThrowAsync(async () => await distributor.ReconfigureAsync());
    }

    [Fact]
    public async Task ReconfigureAsync_PassesCancellationToken()
    {
        // CT propagation: the async API surface MUST forward CT to the data
        // provider so a cancelled controller call doesn't trail a runaway
        // DB query in the background.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<string>>(new List<string>());
            });

        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _distributor.ReconfigureAsync(cts.Token));
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
