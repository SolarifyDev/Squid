using System.Linq;
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

    // ── P0-Phase9.2 startup-race serialization ──────────────────────────────
    //
    // The bug pre-Phase-9.3:
    //   1. Start() schedules the initial DB load on a background Task.Run.
    //   2. Concurrent registration arrives before the initial load finishes.
    //   3. Registration calls ReconfigureIfMissingAsync → ReconfigureAsync → reads
    //      thumbprints {A, B, C} → calls TrustOnly({A, B, C}).
    //   4. Initial load finishes its DB read with the OLDER set {A, B} → calls
    //      TrustOnly({A, B}) → C drops out of trust.
    //   5. C's polling agent is rejected on next attempt until the next reconfigure.
    //
    // Fix: serialize all (read-thumbprints + TrustOnly) pairs via a SemaphoreSlim
    // so the two operations are atomic relative to each other. The fast-path
    // IsTrusted check stays outside the lock for performance.

    [Fact]
    public async Task ReconfigureAsync_ConcurrentCalls_LastWriterPreservesAllThumbprints()
    {
        // Reproduce the exact race: two concurrent reconfigure calls, the FIRST
        // returns the OLDER list (A, B) but completes LATER; the SECOND returns
        // the NEWER list (A, B, C). Without serialization, last-TrustOnly wins
        // by wall-clock ordering and C may drop. With serialization, the two
        // (read+trust) pairs run sequentially and the final state always
        // reflects whichever provider call ran SECOND through the lock.
        var firstCallReady = new TaskCompletionSource();
        var firstCallRelease = new TaskCompletionSource();
        var firstCallStarted = false;
        var callIndex = 0;

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken _) =>
            {
                var idx = Interlocked.Increment(ref callIndex);

                if (idx == 1)
                {
                    firstCallStarted = true;
                    firstCallReady.SetResult();
                    await firstCallRelease.Task;  // hold first call
                    return (IReadOnlyList<string>)new List<string> { "A", "B" };  // older list
                }

                // Second caller: returns newer list with C added
                return new List<string> { "A", "B", "C" };
            });

        // Start first call (will hold inside provider)
        var firstTask = _distributor.ReconfigureAsync();
        await firstCallReady.Task;
        firstCallStarted.ShouldBeTrue();

        // Start second call concurrently — without the serializing lock this
        // would race ahead and call TrustOnly({A,B,C}) before first call's
        // TrustOnly({A,B}) overwrites it.
        var secondTask = _distributor.ReconfigureAsync();

        // Release first call so it can complete TrustOnly({A,B})
        firstCallRelease.SetResult();

        await Task.WhenAll(firstTask, secondTask);

        // With serialization: second call ran AFTER first finished, so final
        // state has {A, B, C} regardless of provider-call ordering.
        // Without serialization: race could leave C un-trusted.
        _halibutRuntime.IsTrusted("A").ShouldBeTrue();
        _halibutRuntime.IsTrusted("B").ShouldBeTrue();
        _halibutRuntime.IsTrusted("C").ShouldBeTrue(customMessage:
            "C must remain trusted after concurrent reconfigure — pre-Phase-9.3 race could drop it.");
    }

    [Fact]
    public async Task ReconfigureAsync_HighConcurrency_NoLostUpdates()
    {
        // 50 concurrent calls each return a slightly different list. After
        // serialization, the final TrustOnly() output reflects ONE of those
        // call's lists exactly (whichever ran last through the lock) — we
        // never get a torn write or a "merged" set.
        var callCount = 0;

        _machineDataProvider
            .Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                var n = Interlocked.Increment(ref callCount);
                return Task.FromResult<IReadOnlyList<string>>(new List<string> { $"thumb-{n}" });
            });

        var tasks = Enumerable.Range(0, 50)
            .Select(_ => _distributor.ReconfigureAsync())
            .ToList();

        await Task.WhenAll(tasks);

        // Exactly one thumbprint should be trusted at end (last writer wins)
        var trustedCount = Enumerable.Range(1, 50)
            .Count(n => _halibutRuntime.IsTrusted($"thumb-{n}"));

        trustedCount.ShouldBe(1, customMessage:
            "Exactly one thumbprint should remain trusted (last call's list) — " +
            "more than one means TrustOnly was called with merged data (impossible " +
            "without lock corruption); zero means concurrent calls clobbered each other.");
    }

    [Fact]
    public async Task Start_BlocksOnInitialLoadCompletion_BeforeServingConcurrentRegistration()
    {
        // Start fires the initial load on background. A concurrent
        // ReconfigureIfMissingAsync arriving DURING that load must wait for
        // the initial load's lock to release rather than racing past it. This
        // test pins: initial load is the FIRST to call TrustOnly, and any
        // concurrent registration's TrustOnly comes strictly after.
        var initialLoadTrustOnlyCalled = new TaskCompletionSource();
        var initialLoadRelease = new TaskCompletionSource();
        var callOrder = new List<string>();

        _machineDataProvider
            .SetupSequence(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                lock (callOrder) callOrder.Add("initial-load-read");
                await initialLoadRelease.Task;
                return (IReadOnlyList<string>)new List<string> { "INITIAL-A" };
            })
            .Returns(async () =>
            {
                lock (callOrder) callOrder.Add("registration-read");
                await Task.Yield();
                return (IReadOnlyList<string>)new List<string> { "INITIAL-A", "REGISTERED-B" };
            });

        _distributor.Start();

        // Wait until initial load has started reading
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            lock (callOrder) if (callOrder.Count > 0) break;
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException();
            await Task.Delay(10);
        }

        // Concurrent registration starts while initial load is still holding the lock
        var regTask = _distributor.ReconfigureIfMissingAsync("REGISTERED-B");

        // Registration must NOT be able to read the DB until initial load
        // finishes its (read + TrustOnly). With the serializing lock, the
        // second 'registration-read' entry should NOT yet appear.
        await Task.Delay(50);  // give the registration task a chance to race
        lock (callOrder) callOrder.Count.ShouldBe(1, customMessage:
            "Registration must not read DB while initial load holds the lock.");

        // Release initial load → it finishes → registration takes the lock
        initialLoadRelease.SetResult();

        await WaitForInitialLoadAsync();
        await regTask;

        lock (callOrder) callOrder.ShouldBe(new[] { "initial-load-read", "registration-read" });
        _halibutRuntime.IsTrusted("INITIAL-A").ShouldBeTrue();
        _halibutRuntime.IsTrusted("REGISTERED-B").ShouldBeTrue();
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
