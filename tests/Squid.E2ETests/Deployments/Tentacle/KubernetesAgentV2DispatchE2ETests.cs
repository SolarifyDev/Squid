using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using NSubstitute;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Tentacle.Dispatchers;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Settings.Halibut;
using Squid.Message.Constants;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V2;
using Squid.Message.Models.Deployments.Execution;
using Squid.Tentacle.Abstractions;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Core;
using Squid.Tentacle.Halibut;
using Squid.Tentacle.ScriptExecution;
using Squid.Tentacle.ScriptExecution.State;
using Xunit;

namespace Squid.E2ETests.Deployments.Tentacle;

/// <summary>
/// Fidelity tier: HIGH for the server side and the wire, MEDIUM-MOCK for the agent's script backend. The REAL
/// <see cref="KubernetesAgentHealthCheckStrategy"/>, recorder, selector, dispatchers, observer and
/// <see cref="HalibutMachineExecutionStrategy"/> talk over a real Halibut POLLING connection (the agent dials the server's
/// listener, mTLS, <c>poll://</c> subscription, as a Kubernetes agent does) to a REAL <see cref="TentacleHalibutHost"/> that advertises V2.
/// Its script backend is the real <see cref="LocalScriptService"/> running real bash, NOT the pod-launching <c>ScriptPodService</c> (no
/// cluster here); the pod service's V2 semantics (pending queue, resolved-on-recovery outcomes) are pinned in the Tentacle test project.
/// Only the checkpoint store and the persisted-capabilities store are in-memory (covered by the integration tier).
///
/// <para>Diagnosing a failure: every wait carries an explicit timeout. The Kubernetes health check is the only thing that fills the
/// capabilities cache for this transport; if V2 is never chosen, check that it ran and stored the agent's <c>SupportedServices</c>.</para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class KubernetesAgentV2DispatchE2ETests : IAsyncLifetime
{
    private static int _machineSeed = 94000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private readonly HalibutTimeoutsAndLimits _limits = HalibutTimeoutsAndLimits.RecommendedValues();
    private readonly List<IAsyncDisposable> _disposables = new();
    private X509Certificate2 _serverCert = null!;
    private HalibutRuntime _server = null!;
    private int _serverPort;
    private CountingFactory _factory = null!;

    public Task InitializeAsync()
    {
        _serverCert = NewCert("CN=k8s-v2-e2e-server");
        _server = new HalibutRuntimeBuilder().WithServerCertificate(_serverCert).WithHalibutTimeoutsAndLimits(_limits).Build();
        _serverPort = _server.Listen();
        _factory = new CountingFactory(new HalibutClientFactory(_server));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try { await disposable.DisposeAsync(); }
            catch (ObjectDisposedException) { }
        }

        await _server.DisposeAsync();
    }

    [Fact]
    public async Task KubernetesAgent_BeforeAnyHealthCheck_IsDispatchedOverV1_ThenTheHealthCheckStoresItsCapabilities_AndItIsDispatchedOverV2()
    {
        var agent = StartPollingAgent();
        var machine = NewMachine(agent);
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var run = new Run(machine, cache, _factory);

        var cold = await run.Strategy().ExecuteScriptAsync(Request(machine, "echo cold-start; exit 0"), CancellationToken.None).WaitAsync(Wait);

        cold.Success.ShouldBeTrue();
        _factory.V2Opened.ShouldBe(0, "before the first health check nothing is advertised, so V1 — which every agent speaks — is used");
        agent.V2.StartCalls.ShouldBe(0);

        var health = await run.HealthCheck().CheckHealthAsync(machine, null, CancellationToken.None).WaitAsync(Wait);

        health.Healthy.ShouldBeTrue(health.Detail);
        cache.TryGet(machine.Id).SupportsScriptServiceV2.ShouldBeTrue("the Kubernetes health check must store what the agent advertised, or the selector can never choose V2 for it");

        var warm = await run.Strategy().ExecuteScriptAsync(Request(machine, "echo hello-over-v2; exit 5"), CancellationToken.None).WaitAsync(Wait);

        warm.ExitCode.ShouldBe(5, "a real script's exit code must survive the V2 round trip over the polling connection");
        warm.LogLines.ShouldContain("hello-over-v2");
        agent.V2.StartCalls.ShouldBe(1, "once the capabilities are stored the same agent is dispatched over V2");
        run.Store.RecordedVersions.ShouldBe(new[] { ScriptProtocolVersion.V1, ScriptProtocolVersion.V2 });
    }

    [Fact]
    public async Task KubernetesAgent_WhoseHealthCheckSawOnlyV1_IsNeverSentAV2Call()
    {
        var agent = StartPollingAgent(v2: false);
        var machine = NewMachine(agent);
        var cache = new InMemoryMachineRuntimeCapabilitiesCache();
        var run = new Run(machine, cache, _factory);

        (await run.HealthCheck().CheckHealthAsync(machine, null, CancellationToken.None).WaitAsync(Wait)).Healthy.ShouldBeTrue();
        var result = await run.Strategy().ExecuteScriptAsync(Request(machine, "echo v1-agent; exit 0"), CancellationToken.None).WaitAsync(Wait);

        result.Success.ShouldBeTrue();
        cache.TryGet(machine.Id).SupportsScriptServiceV2.ShouldBeFalse();
        _factory.V2Opened.ShouldBe(0, "a V1-only agent answers a V2 call with a plain transport exception");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private PollingAgent StartPollingAgent(bool v2 = true)
    {
        var cert = NewCert("CN=k8s-v2-e2e-agent");
        var local = new LocalScriptService(new ScriptStateStoreFactory());
        var counting = new CountingV2(local);
        IScriptService service = v2 ? new BackendScriptServiceV2Adapter(local, counting) : new BackendScriptServiceAdapter(local);
        var subscriptionId = $"k8s-v2-{Guid.NewGuid():N}";

        var host = new TentacleHalibutHost(cert, service, new TentacleSettings { ServerCommsUrl = $"https://localhost:{_serverPort}/" }, new CapabilitiesService(metadata: null, advertiseScriptServiceV2: service is IScriptServiceV2));
        _server.Trust(cert.Thumbprint);
        host.StartPolling(_serverCert.Thumbprint, subscriptionId);
        _disposables.Add(host);

        return new PollingAgent(subscriptionId, cert.Thumbprint, counting);
    }

    private static Machine NewMachine(PollingAgent agent) => new()
    {
        Id = Interlocked.Increment(ref _machineSeed),
        Name = $"k8s-v2-e2e-{_machineSeed}",
        Endpoint = $$"""{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"{{agent.SubscriptionId}}","Thumbprint":"{{agent.Thumbprint}}"}"""
    };

    private static ScriptExecutionRequest Request(Machine machine, string script) => new()
    {
        Machine = machine,
        ScriptBody = script,
        ExecutionMode = ExecutionMode.DirectScript,
        Syntax = ScriptSyntax.Bash,
        ReleaseVersion = "1.0.0",
        ServerTaskId = 4444,
        StepId = 11,
        ActionId = 22,
        StepName = "step",
        ActionName = "action",
        Timeout = TimeSpan.FromSeconds(80),
        Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
    };

    private static X509Certificate2 NewCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private sealed record PollingAgent(string SubscriptionId, string Thumbprint, CountingV2 V2);

    private sealed class Run
    {
        private readonly InMemoryMachineRuntimeCapabilitiesCache _cache;
        private readonly CountingFactory _factory;
        private readonly DirectStoreAccess _store;

        public Run(Machine machine, InMemoryMachineRuntimeCapabilitiesCache cache, CountingFactory factory)
        {
            Machine = machine;
            _cache = cache;
            _factory = factory;
            _store = new DirectStoreAccess(Substitute.For<IMachineRuntimeCapabilitiesPersistence>());
            Store = new InMemoryStore();
        }

        public Machine Machine { get; }
        public InMemoryStore Store { get; }

        public KubernetesAgentHealthCheckStrategy HealthCheck() => new(_factory, new MachineCapabilitiesRecorder(_cache, _store));

        public HalibutMachineExecutionStrategy Strategy()
        {
            var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 100, MaxPollIntervalMs = 500 });
            var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(_factory), new V2ScriptDispatcher(_factory) }, _cache);

            return new HalibutMachineExecutionStrategy(_factory, new CalamariPayloadBuilder(Substitute.For<IYamlNuGetPacker>()), observer, new HalibutSetting(), inFlightStore: Store, dispatcherSelector: selector);
        }
    }

    private sealed class DirectStoreAccess : IMachineCapabilitiesStoreAccess
    {
        private readonly IMachineRuntimeCapabilitiesPersistence _persistence;

        public DirectStoreAccess(IMachineRuntimeCapabilitiesPersistence persistence) => _persistence = persistence;

        public Task<T> RunAsync<T>(Func<IMachineRuntimeCapabilitiesPersistence, Task<T>> operation) => operation(_persistence);
    }

    /// <summary>Counts which protocol's client the server opened, delegating to the real Halibut factory.</summary>
    private sealed class CountingFactory : IHalibutClientFactory, IScriptServiceV2ClientFactory
    {
        private readonly HalibutClientFactory _inner;

        public CountingFactory(HalibutClientFactory inner) => _inner = inner;

        public int V2Opened { get; private set; }

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint) => _inner.CreateClient(endpoint);

        public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint)
        {
            V2Opened++;
            return _inner.CreateScriptServiceV2Client(endpoint);
        }

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => _inner.CreateCapabilitiesClient(endpoint);

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => _inner.CreateFileTransferClient(endpoint);
    }

    private sealed class InMemoryStore : IInFlightScriptStore
    {
        private (string Ticket, ScriptProtocolVersion Version)? _record;

        public List<ScriptProtocolVersion> RecordedVersions { get; } = new();

        public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, CancellationToken cancellationToken = default)
            => RecordDispatchedAsync(serverTaskId, slot, scriptTicket, ScriptProtocolVersion.V1, cancellationToken);

        public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, ScriptProtocolVersion protocol, CancellationToken cancellationToken = default)
        {
            _record = (scriptTicket, protocol);
            RecordedVersions.Add(protocol);

            return Task.CompletedTask;
        }

        public Task ClearAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default)
        {
            _record = null;

            return Task.CompletedTask;
        }

        public Task<string> TryGetTicketAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default) => Task.FromResult(_record?.Ticket);

        public Task<ScriptProtocolVersion?> TryGetProtocolAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default) => Task.FromResult<ScriptProtocolVersion?>(_record?.Version);

        public Task<bool> IsMachineBusyAsync(int machineId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CountingV2 : IScriptServiceV2
    {
        private readonly IScriptServiceV2 _inner;
        private int _startCalls;

        public CountingV2(IScriptServiceV2 inner) => _inner = inner;

        public int StartCalls => _startCalls;

        public ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command)
        {
            Interlocked.Increment(ref _startCalls);

            return _inner.StartScript(command);
        }

        public ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request) => _inner.GetStatus(request);

        public ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command) => _inner.CancelScript(command);

        public CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command) => _inner.CompleteScript(command);
    }
}
