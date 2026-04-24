using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Certificate;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.Health;
using Squid.Tentacle.Tests.Support;
using Squid.Tentacle.Tests.Support.Fakes;

namespace Squid.Tentacle.Tests.Core;

[Trait("Category", TentacleTestCategories.Core)]
public class TentacleAppTests : TimedTestBase
{
    [Fact]
    public async Task RunAsync_Starts_And_Wires_Core_Lifecycle_Then_Shuts_Down_On_Cancel()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "sub-xyz");
        var registrar = new FakeRegistrar
        {
            Result = new TentacleRegistration
            {
                MachineId = 42,
                ServerThumbprint = "server-thumb",
                SubscriptionUri = "poll://sub-xyz/"
            }
        };
        var backend = new FakeScriptBackend();
        var hook = new FakeStartupHook("hook-1");
        var backgroundTask = new FakeBackgroundTask("bg-1");
        var flavor = new FakeFlavor("KubernetesAgent", registrar, backend, [hook], [backgroundTask]);

        var halibutHost = new FakeHalibutHost();
        var healthServer = new FakeHealthServer();

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "KubernetesAgent", ServerCommsUrl = "https://localhost:10943" };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await backgroundTask.Started.Task.WaitAsync(TestCancellationToken);
        cts.Cancel();
        await runTask;

        registrar.Calls.ShouldBe(1);
        registrar.LastIdentity.SubscriptionId.ShouldBe("sub-xyz");
        registrar.LastIdentity.Thumbprint.ShouldBe(cert.Thumbprint);

        halibutHost.StartCalls.ShouldBe(1);
        halibutHost.ServerThumbprint.ShouldBe("server-thumb");
        halibutHost.SubscriptionId.ShouldBe("sub-xyz");
        halibutHost.SubscriptionUri.ShouldBe("poll://sub-xyz/");
        halibutHost.Disposed.ShouldBeTrue();

        healthServer.StartCalls.ShouldBe(1);
        healthServer.Disposed.ShouldBeTrue();

        hook.Calls.ShouldBe(1);
        backgroundTask.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_ListeningMode_Calls_StartListening_With_Correct_Port()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "sub-listen");
        var registrar = new FakeRegistrar();
        var backend = new FakeScriptBackend();
        var hook = new FakeStartupHook("hook-listen");
        var flavor = new FakeFlavor("ListeningAgent", registrar, backend, [hook], [],
            communicationMode: TentacleCommunicationMode.Listening,
            listeningPort: 12345);

        var halibutHost = new FakeHalibutHost();
        var healthServer = new FakeHealthServer();

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "ListeningAgent" };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);
        cts.Cancel();
        await runTask;

        halibutHost.StartCalls.ShouldBe(1);
        halibutHost.ListeningPort.ShouldBe(12345);
        halibutHost.ServerThumbprint.ShouldBe(string.Empty);
        halibutHost.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_ListeningMode_Falls_Back_To_Settings_Port_When_Runtime_Port_Is_Null()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "sub-listen-default");
        var registrar = new FakeRegistrar();
        var backend = new FakeScriptBackend();
        var hook = new FakeStartupHook("hook-listen-default");
        var flavor = new FakeFlavor("ListeningAgent", registrar, backend, [hook], [],
            communicationMode: TentacleCommunicationMode.Listening);

        var halibutHost = new FakeHalibutHost();
        var healthServer = new FakeHealthServer();

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "ListeningAgent", ListeningPort = 19999 };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);
        cts.Cancel();
        await runTask;

        halibutHost.StartCalls.ShouldBe(1);
        halibutHost.ListeningPort.ShouldBe(19999);
    }

    [Fact]
    public async Task RunAsync_Continues_When_HealthServer_Start_Fails()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "sub-health-fail");
        var registrar = new FakeRegistrar();
        var backend = new FakeScriptBackend();
        var hook = new FakeStartupHook("hook-health-fail");
        var flavor = new FakeFlavor("KubernetesAgent", registrar, backend, [hook], []);
        var halibutHost = new FakeHalibutHost();
        var healthServer = new FakeHealthServer { ThrowOnStart = true };

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "KubernetesAgent", ServerCommsUrl = "https://localhost:10943" };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);
        cts.Cancel();
        await runTask;

        hook.Calls.ShouldBe(1);
        halibutHost.StartCalls.ShouldBe(1);
        healthServer.StartCalls.ShouldBe(1);
    }

    [Fact]
    public async Task RunAsync_ExternalSubscriptionId_OverridesTakesPrecedence()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "file-sub-id");
        var registrar = new FakeRegistrar
        {
            Result = new TentacleRegistration
            {
                MachineId = 99,
                ServerThumbprint = "server-thumb",
                SubscriptionUri = "poll://external-sub-id/"
            }
        };
        var backend = new FakeScriptBackend();
        var hook = new FakeStartupHook("hook-override");
        var flavor = new FakeFlavor("KubernetesAgent", registrar, backend, [hook], []);

        var halibutHost = new FakeHalibutHost();
        var healthServer = new FakeHealthServer();

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "KubernetesAgent", SubscriptionId = "external-sub-id", ServerCommsUrl = "https://localhost:10943" };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);
        cts.Cancel();
        await runTask;

        registrar.LastIdentity.SubscriptionId.ShouldBe("external-sub-id");
        halibutHost.SubscriptionId.ShouldBe("external-sub-id");
    }

    [Fact]
    public async Task Shutdown_FlipsReadinessToFalse()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "sub-readiness");
        var registrar = new FakeRegistrar();
        var drainableBackend = new FakeDrainableScriptBackend();
        var hook = new FakeStartupHook("hook-ready");
        var flavor = new FakeFlavor("KubernetesAgent", registrar, drainableBackend, [hook], [],
            readinessCheck: () => true);

        var halibutHost = new FakeHalibutHost();
        Func<bool> capturedReadiness = null;
        var healthServer = new FakeHealthServer();

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (port, readiness) =>
            {
                capturedReadiness = readiness;
                return healthServer;
            }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "KubernetesAgent", ServerCommsUrl = "https://localhost:10943" };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);

        capturedReadiness.ShouldNotBeNull();
        capturedReadiness().ShouldBeTrue();

        cts.Cancel();
        await runTask;

        // After shutdown, readiness should be false (isReady = false)
        capturedReadiness().ShouldBeFalse();
    }

    [Fact]
    public async Task Shutdown_CallsDrainOnScriptBackend()
    {
        using var cert = CreateCertificate();
        var certManager = new FakeCertificateManager(cert, "sub-drain");
        var registrar = new FakeRegistrar();
        var drainableBackend = new FakeDrainableScriptBackend();
        var hook = new FakeStartupHook("hook-drain");
        var flavor = new FakeFlavor("KubernetesAgent", registrar, drainableBackend, [hook], []);

        var halibutHost = new FakeHalibutHost();
        var healthServer = new FakeHealthServer();

        var app = new TentacleApp(new TentacleAppDependencies
        {
            CertificateManagerFactory = _ => certManager,
            BuiltInFlavorsProvider = () => [flavor],
            FlavorResolverFactory = flavors => new TentacleFlavorResolver(flavors),
            HalibutHostFactory = (_, _, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var settings = new TentacleSettings { Flavor = "KubernetesAgent", ServerCommsUrl = "https://localhost:10943", ShutdownDrainTimeoutSeconds = 2 };
        var runTask = app.RunAsync(settings, CreateEmptyConfiguration(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);
        cts.Cancel();
        await runTask;

        drainableBackend.DrainCalls.ShouldBe(1);
        drainableBackend.LastDrainTimeout.TotalSeconds.ShouldBe(2);

        // P0-T.4: CancelPolling must fire during shutdown (before drain). If this
        // regresses, the agent keeps picking up new Halibut RPCs during drain and
        // either extends drain past timeout or kills new scripts mid-execution.
        halibutHost.CancelPollingCalls.ShouldBe(1,
            customMessage:
                "Shutdown must call halibutHost.CancelPolling() exactly once before drain. " +
                "Pre-T.4 the poll loop ran with CancellationToken.None and couldn't be stopped " +
                "until runtime disposal.");
    }

    [Fact]
    public void LoadSettings_Binds_Tentacle_Section()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Tentacle:Flavor"] = "Linux",
                ["Tentacle:HealthCheckPort"] = "18080"
            })
            .Build();

        var tentacle = TentacleApp.LoadTentacleSettings(config);

        tentacle.Flavor.ShouldBe("Linux");
        tentacle.HealthCheckPort.ShouldBe(18080);
    }

    private static IConfiguration CreateEmptyConfiguration()
        => new ConfigurationBuilder().Build();

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
                return;

            await Task.Delay(50, ct);
        }

        condition().ShouldBeTrue("Condition was not satisfied before timeout.");
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private sealed class FakeDrainableScriptBackend : ITentacleScriptBackend, IGracefulShutdownAware
    {
        public int DrainCalls { get; private set; }
        public TimeSpan LastDrainTimeout { get; private set; }

        public ScriptStatusResponse StartScript(StartScriptCommand command) => new(new ScriptTicket("fake-ticket"), ProcessState.Running, 0, new List<ProcessOutput>(), 0);
        public ScriptStatusResponse GetStatus(ScriptStatusRequest request) => new(new ScriptTicket("fake-ticket"), ProcessState.Running, 0, new List<ProcessOutput>(), 0);
        public ScriptStatusResponse CompleteScript(CompleteScriptCommand command) => new(new ScriptTicket("fake-ticket"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0);
        public ScriptStatusResponse CancelScript(CancelScriptCommand command) => new(new ScriptTicket("fake-ticket"), ProcessState.Complete, -1, new List<ProcessOutput>(), 0);

        public Task WaitForDrainAsync(TimeSpan timeout)
        {
            DrainCalls++;
            LastDrainTimeout = timeout;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeFlavor : ITentacleFlavor
    {
        private readonly TentacleFlavorRuntime _runtime;

        public FakeFlavor(
            string id,
            ITentacleRegistrar registrar,
            ITentacleScriptBackend backend,
            IReadOnlyList<ITentacleStartupHook> hooks,
            IReadOnlyList<ITentacleBackgroundTask> backgroundTasks,
            TentacleCommunicationMode communicationMode = TentacleCommunicationMode.Polling,
            int? listeningPort = null,
            Func<bool> readinessCheck = null)
        {
            Id = id;
            _runtime = new TentacleFlavorRuntime
            {
                Registrar = registrar,
                ScriptBackend = backend,
                StartupHooks = hooks,
                BackgroundTasks = backgroundTasks,
                CommunicationMode = communicationMode,
                ListeningPort = listeningPort,
                ReadinessCheck = readinessCheck
            };
        }

        public string Id { get; }

        public TentacleFlavorRuntime CreateRuntime(TentacleFlavorContext context) => _runtime;
    }

    private sealed class FakeCertificateManager : ITentacleCertificateManager
    {
        private readonly X509Certificate2 _certificate;
        private readonly string _subscriptionId;

        public FakeCertificateManager(X509Certificate2 certificate, string subscriptionId)
        {
            _certificate = certificate;
            _subscriptionId = subscriptionId;
        }

        public X509Certificate2 LoadOrCreateCertificate() => _certificate;

        public string LoadOrCreateSubscriptionId(string overrideSubscriptionId = null) =>
            string.IsNullOrWhiteSpace(overrideSubscriptionId) ? _subscriptionId : overrideSubscriptionId;
    }

    private sealed class FakeHalibutHost : ITentacleHalibutHost
    {
        public int StartCalls { get; private set; }
        public string ServerThumbprint { get; private set; } = string.Empty;
        public string SubscriptionId { get; private set; } = string.Empty;
        public string SubscriptionUri { get; private set; } = string.Empty;
        public int ListeningPort { get; private set; }
        public bool IsListening { get; private set; }
        public bool Disposed { get; private set; }
        public int CancelPollingCalls { get; private set; }

        public void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null)
        {
            StartCalls++;
            ServerThumbprint = serverThumbprint;
            SubscriptionId = subscriptionId;
            SubscriptionUri = subscriptionUri ?? string.Empty;
        }

        public void StartListening(int port, string serverThumbprint = null)
        {
            StartCalls++;
            ListeningPort = port;
            IsListening = true;
        }

        public void CancelPolling() => CancelPollingCalls++;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHealthServer : IHealthCheckServer
    {
        public bool ThrowOnStart { get; set; }
        public int StartCalls { get; private set; }
        public bool Disposed { get; private set; }

        public void Start()
        {
            StartCalls++;
            if (ThrowOnStart)
                throw new InvalidOperationException("boom");
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
