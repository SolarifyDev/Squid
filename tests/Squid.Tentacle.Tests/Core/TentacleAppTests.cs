using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
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
            HalibutHostFactory = (_, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var runTask = app.RunAsync(new TentacleSettings(), new KubernetesSettings(), cts.Token);

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
            HalibutHostFactory = (_, _, _) => halibutHost,
            HealthCheckServerFactory = (_, _) => healthServer
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        var runTask = app.RunAsync(new TentacleSettings(), new KubernetesSettings(), cts.Token);

        await WaitUntilAsync(() => hook.Calls == 1, TestCancellationToken);
        cts.Cancel();
        await runTask;

        hook.Calls.ShouldBe(1);
        halibutHost.StartCalls.ShouldBe(1);
        healthServer.StartCalls.ShouldBe(1);
    }

    [Fact]
    public void LoadSettings_Binds_Tentacle_And_Kubernetes_Sections()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Tentacle:Flavor"] = "Linux",
                ["Tentacle:HealthCheckPort"] = "18080",
                ["Kubernetes:Namespace"] = "qa",
                ["Kubernetes:UseScriptPods"] = "true"
            })
            .Build();

        var tentacle = TentacleApp.LoadTentacleSettings(config);
        var kubernetes = TentacleApp.LoadKubernetesSettings(config);

        tentacle.Flavor.ShouldBe("Linux");
        tentacle.HealthCheckPort.ShouldBe(18080);
        kubernetes.Namespace.ShouldBe("qa");
        kubernetes.UseScriptPods.ShouldBeTrue();
    }

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
#pragma warning disable SYSLIB0057
        return new X509Certificate2(cert.Export(X509ContentType.Pfx));
#pragma warning restore SYSLIB0057
    }

    private sealed class FakeFlavor : ITentacleFlavor
    {
        private readonly TentacleFlavorRuntime _runtime;

        public FakeFlavor(
            string id,
            ITentacleRegistrar registrar,
            ITentacleScriptBackend backend,
            IReadOnlyList<ITentacleStartupHook> hooks,
            IReadOnlyList<ITentacleBackgroundTask> backgroundTasks)
        {
            Id = id;
            _runtime = new TentacleFlavorRuntime
            {
                Registrar = registrar,
                ScriptBackend = backend,
                StartupHooks = hooks,
                BackgroundTasks = backgroundTasks
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

        public string LoadOrCreateSubscriptionId() => _subscriptionId;
    }

    private sealed class FakeHalibutHost : ITentacleHalibutHost
    {
        public int StartCalls { get; private set; }
        public string ServerThumbprint { get; private set; } = string.Empty;
        public string SubscriptionId { get; private set; } = string.Empty;
        public string SubscriptionUri { get; private set; } = string.Empty;
        public bool Disposed { get; private set; }

        public void StartPolling(string serverThumbprint, string subscriptionId, string subscriptionUri = null)
        {
            StartCalls++;
            ServerThumbprint = serverThumbprint;
            SubscriptionId = subscriptionId;
            SubscriptionUri = subscriptionUri ?? string.Empty;
        }

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
