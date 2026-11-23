using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Autofac;
using Halibut;
using Microsoft.Extensions.Configuration;
using Squid.Core.Halibut;
using Squid.Core.Services.Machines;
using Squid.Core.Settings.Halibut;
using Squid.Core.Settings.SelfCert;

namespace Squid.UnitTests.Halibut;

public class PollingTrustRefresherTests
{
    // ── Settings binding ────────────────────────────────────────────────

    [Fact]
    public void Settings_BindFromTheHalibutTrustSection()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Halibut:Trust:RefreshIntervalSeconds"] = "7",
            ["Halibut:Trust:UnauthorizedRefreshMinIntervalSeconds"] = "3"
        }).Build();

        var trust = new HalibutSetting(configuration).Trust;

        trust.RefreshInterval.ShouldBe(TimeSpan.FromSeconds(7));
        trust.UnauthorizedRefreshMinInterval.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Settings_WhenSectionAbsent_UseThePinnedDefaults()
    {
        var trust = new HalibutSetting(new ConfigurationBuilder().Build()).Trust;

        trust.RefreshInterval.ShouldBe(TimeSpan.FromSeconds(15));
        trust.UnauthorizedRefreshMinInterval.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Settings_NonPositiveValues_FallBackToTheDefaults(string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["Halibut:Trust:RefreshIntervalSeconds"] = value,
            ["Halibut:Trust:UnauthorizedRefreshMinIntervalSeconds"] = value
        }).Build();

        var trust = new HalibutSetting(configuration).Trust;

        trust.RefreshInterval.ShouldBe(TimeSpan.FromSeconds(15));
        trust.UnauthorizedRefreshMinInterval.ShouldBe(TimeSpan.FromSeconds(2));
    }

    // ── Unauthorized-connect trigger ────────────────────────────────────

    [Fact]
    public async Task OnUnauthorizedClient_RefreshesFromTheDatabase()
    {
        var distributor = new CountingDistributor();
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings(), new ManualTimeProvider());

        refresher.OnUnauthorizedClient("UNKNOWN");

        await distributor.WaitForCallsAsync(1);
    }

    [Fact]
    public async Task OnUnauthorizedClient_IsRateLimited_AndResumesAfterTheMinimumInterval()
    {
        var clock = new ManualTimeProvider();
        var distributor = new CountingDistributor();
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings { UnauthorizedRefreshMinIntervalSeconds = 10 }, clock);

        for (var i = 0; i < 50; i++) refresher.OnUnauthorizedClient("UNKNOWN");

        await distributor.WaitForCallsAsync(1);
        await Task.Delay(200);

        distributor.Calls.ShouldBe(1, "a burst of unknown clients must cost one database read, not one per connection");

        clock.Advance(TimeSpan.FromSeconds(9));
        refresher.OnUnauthorizedClient("UNKNOWN");
        await Task.Delay(200);

        distributor.Calls.ShouldBe(1, "still inside the minimum interval");

        clock.Advance(TimeSpan.FromSeconds(2));
        refresher.OnUnauthorizedClient("UNKNOWN");

        await distributor.WaitForCallsAsync(2);
    }

    [Fact]
    public async Task OnUnauthorizedClient_ClientThatStaysUnknown_BacksOffExponentially_UpToTheRefreshInterval()
    {
        var clock = new ManualTimeProvider();
        var distributor = new CountingDistributor();
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings { UnauthorizedRefreshMinIntervalSeconds = 2, RefreshIntervalSeconds = 15 }, clock);
        var expectedCalls = 0;

        async Task HitAsync(string thumbprint, bool expectRefresh)
        {
            refresher.OnUnauthorizedClient(thumbprint);

            if (expectRefresh) await distributor.WaitForCallsAsync(++expectedCalls);
            else await Task.Delay(100);

            distributor.Calls.ShouldBe(expectedCalls);
        }

        await HitAsync("GONE", expectRefresh: true);      // t=0, next gap 2s
        clock.Advance(TimeSpan.FromSeconds(3));
        await HitAsync("GONE", expectRefresh: true);      // t=3, next gap 4s
        clock.Advance(TimeSpan.FromSeconds(3));
        await HitAsync("GONE", expectRefresh: false);     // t=6: only 3s since the last, gap is 4s
        clock.Advance(TimeSpan.FromSeconds(2));
        await HitAsync("GONE", expectRefresh: true);      // t=8, next gap 8s
        clock.Advance(TimeSpan.FromSeconds(5));
        await HitAsync("GONE", expectRefresh: false);     // t=13: 5s < 8s

        await HitAsync("OTHER", expectRefresh: true);     // a different client is not held back by GONE's backoff
    }

    [Fact]
    public async Task OnUnauthorizedClient_BackoffResets_AfterTheClientWasQuietForLong()
    {
        var clock = new ManualTimeProvider();
        var distributor = new CountingDistributor();
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings { UnauthorizedRefreshMinIntervalSeconds = 2, RefreshIntervalSeconds = 15 }, clock);

        refresher.OnUnauthorizedClient("GONE");
        await distributor.WaitForCallsAsync(1);
        clock.Advance(TimeSpan.FromSeconds(3));
        refresher.OnUnauthorizedClient("GONE");
        await distributor.WaitForCallsAsync(2);

        clock.Advance(TimeSpan.FromSeconds(120));
        refresher.OnUnauthorizedClient("GONE");
        await distributor.WaitForCallsAsync(3);

        clock.Advance(TimeSpan.FromSeconds(3));
        refresher.OnUnauthorizedClient("GONE");

        await distributor.WaitForCallsAsync(4);
    }

    // ── Timer ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_RefreshesPeriodically()
    {
        var distributor = new CountingDistributor();
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings { RefreshIntervalSeconds = 1 }, TimeProvider.System);

        refresher.Start();

        await distributor.WaitForCallsAsync(2, TimeSpan.FromSeconds(10));
    }

    // ── Refresh semantics ───────────────────────────────────────────────

    [Fact]
    public async Task RefreshAsync_WhenTheDistributorThrows_DoesNotThrow_AndTheNextRefreshStillRuns()
    {
        var distributor = new CountingDistributor { FailNext = true };
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings(), new ManualTimeProvider());

        await refresher.RefreshAsync();
        await refresher.RefreshAsync();

        distributor.Calls.ShouldBe(2);
    }

    [Fact]
    public async Task RefreshAsync_RequestsDuringAnInFlightRefresh_CoalesceIntoOneFollowUp()
    {
        var distributor = new CountingDistributor { Gate = new TaskCompletionSource() };
        using var refresher = new PollingTrustRefresher(distributor, new TrustSettings(), new ManualTimeProvider());

        var first = refresher.RefreshAsync();
        await distributor.WaitForCallsAsync(1);

        for (var i = 0; i < 5; i++) await refresher.RefreshAsync();

        distributor.Calls.ShouldBe(1, "requests while one is in flight must not start parallel reads");

        distributor.Gate.SetResult();
        await first;

        distributor.Calls.ShouldBe(2, "the in-flight read may predate the change that prompted the request, so exactly one follow-up must run");
    }

    // ── Behaviour against the real distributor + provider + runtime ─────

    [Fact]
    public async Task FailedRefresh_KeepsThePreviousSnapshot()
    {
        using var pod = new Pod(new[] { "A", "B" });

        await pod.Refresher.RefreshAsync();

        pod.Provider.IsTrusted("A").ShouldBeTrue();

        var version = pod.Provider.Version;

        pod.Machines.Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("db blip"));

        await pod.Refresher.RefreshAsync();

        pod.Provider.IsTrusted("A").ShouldBeTrue("a failed refresh must never clear trust");
        pod.Provider.IsTrusted("B").ShouldBeTrue();
        pod.Provider.Version.ShouldBe(version);
    }

    [Fact]
    public async Task OlderSlowRefresh_CannotOverwriteANewerSnapshot()
    {
        using var pod = new Pod(new[] { "OLD" });

        var releaseOlder = new TaskCompletionSource();
        var calls = 0;

        pod.Machines.Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                await releaseOlder.Task;
                return new List<string> { "OLD" };
            }

            return new List<string> { "NEW" };
        });

        var older = pod.Distributor.ReconfigureAsync();
        var newer = pod.Distributor.ReconfigureAsync();

        await Task.Delay(100);
        releaseOlder.SetResult();
        await Task.WhenAll(older, newer);

        pod.Provider.IsTrusted("NEW").ShouldBeTrue("the later refresh read the database after the earlier one and must win");
        pod.Provider.IsTrusted("OLD").ShouldBeFalse();
    }

    [Fact]
    public async Task IsTrusted_NeverTouchesTheDatabase()
    {
        using var pod = new Pod(new[] { "A" });

        await pod.Refresher.RefreshAsync();

        pod.Machines.Invocations.Clear();

        for (var i = 0; i < 1000; i++)
        {
            pod.Runtime.IsTrusted("A").ShouldBeTrue();
            pod.Runtime.IsTrusted("UNKNOWN").ShouldBeFalse();
        }

        pod.Machines.Invocations.ShouldBeEmpty("trust checks run inside a TLS handshake and must be answered from memory");
    }

    // ── Module wiring (delete any of these call sites and a test here goes red) ──

    [Fact]
    public async Task Module_WiresTheSnapshotProviderIntoTheRuntime()
    {
        using var pod = new Pod(new[] { "A" }, useModule: true);

        await pod.Distributor.ReconfigureAsync();

        pod.Provider.IsTrusted("A").ShouldBeTrue("the runtime built by HalibutModule must use the shared SnapshotTrustProvider");
        pod.Runtime.IsTrusted("A").ShouldBeTrue();
    }

    [Fact]
    public async Task Module_UnauthorizedConnect_BlocksAndTriggersARefresh()
    {
        using var pod = new Pod(Array.Empty<string>(), useModule: true);

        await Poll.UntilAsync(() => pod.Distributor.InitialLoadCompleted, TimeSpan.FromSeconds(10), "startup trust load did not finish");

        pod.Current = new[] { "NEWLY-REGISTERED-ELSEWHERE" };

        var response = pod.Runtime.OnUnauthorizedClientConnect("client", "NEWLY-REGISTERED-ELSEWHERE");

        response.ShouldBe(UnauthorizedClientConnectResponse.BlockConnection);

        await Poll.UntilAsync(() => pod.Provider.IsTrusted("NEWLY-REGISTERED-ELSEWHERE"), TimeSpan.FromSeconds(10), "the unauthorized-connect callback must trigger a refresh from the database");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private sealed class CountingDistributor : IPollingTrustDistributor
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public bool FailNext { get; set; }
        public TaskCompletionSource Gate { get; set; }
        public bool InitialLoadCompleted => true;

        public async Task ReconfigureAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);

            if (Gate != null) await Gate.Task;

            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("db blip");
            }
        }

        public async Task WaitForCallsAsync(int expected, TimeSpan? timeout = null)
            => await Poll.UntilAsync(() => Calls >= expected, timeout ?? TimeSpan.FromSeconds(5), $"expected {expected} refresh call(s), saw {Calls}");

        public Task ReconfigureIfMissingAsync(string thumbprint, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Reconfigure() { }
        public void ReconfigureIfMissing(string thumbprint) { }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp = 1_000_000;

        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _timestamp, (long)(by.TotalSeconds * TimestampFrequency));
    }

    private static class Poll
    {
        public static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, string failure)
        {
            var deadline = DateTime.UtcNow + timeout;

            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalSeconds}s: {failure}");

                await Task.Delay(25);
            }
        }
    }

    /// <summary>One server replica: a runtime with the snapshot provider, its distributor and refresher, over a mocked database.</summary>
    private sealed class Pod : IDisposable
    {
        private readonly IContainer _container;

        public Mock<IMachineDataProvider> Machines { get; } = new();
        public volatile IReadOnlyList<string> Current;
        public SnapshotTrustProvider Provider { get; }
        public HalibutRuntime Runtime { get; }
        public PollingTrustDistributor Distributor { get; }
        public PollingTrustRefresher Refresher { get; }

        public Pod(IReadOnlyList<string> thumbprints, bool useModule = false)
        {
            Current = thumbprints;
            Machines.Setup(x => x.GetPollingThumbprintsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => Current.ToList());

            var builder = new ContainerBuilder();
            builder.RegisterInstance(Machines.Object).As<IMachineDataProvider>();

            if (useModule)
            {
                builder.RegisterInstance(new SelfCertSetting { Base64 = Convert.ToBase64String(CreatePfx()), Password = string.Empty }).AsSelf().SingleInstance();
                builder.RegisterInstance(new HalibutSetting()).AsSelf().SingleInstance();
                builder.RegisterModule(new HalibutModule());
                _container = builder.Build();

                Provider = _container.Resolve<SnapshotTrustProvider>();
                Runtime = _container.Resolve<HalibutRuntime>();
                Distributor = (PollingTrustDistributor)_container.Resolve<IPollingTrustDistributor>();
                Refresher = (PollingTrustRefresher)_container.Resolve<IPollingTrustRefresher>();

                return;
            }

            Provider = new SnapshotTrustProvider();
            Runtime = new HalibutRuntimeBuilder().WithServerCertificate(LoadCert()).WithTrustProvider(Provider).Build();
            builder.RegisterInstance(Runtime).As<HalibutRuntime>();
            _container = builder.Build();

            Distributor = new PollingTrustDistributor(_container);
            Refresher = new PollingTrustRefresher(Distributor, new TrustSettings(), TimeProvider.System);
        }

        public void Dispose()
        {
            Refresher.Dispose();
            Runtime.Dispose();
            _container.Dispose();
        }

        private static X509Certificate2 LoadCert() => X509CertificateLoader.LoadPkcs12(CreatePfx(), string.Empty);

        private static byte[] CreatePfx()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=trust-test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(1));

            return cert.Export(X509ContentType.Pfx, string.Empty);
        }
    }
}
