using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using NSubstitute;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
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
/// Fidelity tier: HIGH for the server and the wire, MEDIUM-MIRROR for the "downgrade". The REAL
/// <see cref="HalibutMachineExecutionStrategy"/> with the real selector, dispatchers, observer, capabilities
/// refresher and <see cref="HalibutClientFactory"/> talks over real Halibut listening connections (loopback mTLS) to
/// REAL <see cref="TentacleHalibutHost"/>s wrapping the real <see cref="LocalScriptService"/> that runs real bash.
/// A downgrade is modelled by stopping the V2-serving host and starting a V1-only host (built exactly as the
/// shipped agent builds one without V2: no V2 service registered, V2 not advertised) over the SAME
/// <see cref="LocalScriptService"/> — the agent's script state and running processes survive, as they do across a
/// real downgrade where both versions read the same on-disk state. The agent PROCESS is not actually restarted.
/// Only the checkpoint store and the persisted-capabilities store are in-memory (covered by the integration tier).
///
/// <para>Diagnosing a failure: every wait carries an explicit timeout. The wire exception a V1-only host answers a
/// V2 call with is asserted in <see cref="V1OnlyHost_AnswersAV2Call_WithAnExceptionTheStrategyTreatsAsAProtocolMismatchCandidate"/>;
/// look in the temp dir for <c>squid-tentacle-{ticket}</c> workspaces to inspect the agent's state.</para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class ScriptServiceDowngradeE2ETests : IAsyncLifetime
{
    private static int _machineSeed = 93000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(90);

    private readonly HalibutTimeoutsAndLimits _limits = HalibutTimeoutsAndLimits.RecommendedValues();
    private readonly List<IAsyncDisposable> _disposables = new();
    private X509Certificate2 _serverCert = null!;
    private HalibutRuntime _server = null!;
    private HalibutClientFactory _factory = null!;

    public Task InitializeAsync()
    {
        _serverCert = NewCert("CN=downgrade-e2e-server");
        _server = new HalibutRuntimeBuilder().WithServerCertificate(_serverCert).WithHalibutTimeoutsAndLimits(_limits).Build();
        _factory = new HalibutClientFactory(_server);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables)
        {
            try { await disposable.DisposeAsync(); }
            catch (ObjectDisposedException) { /* a host the test already stopped */ }
        }

        await _server.DisposeAsync();
    }

    [Fact]
    public async Task V1OnlyHost_AnswersAV2Call_WithAnExceptionTheStrategyTreatsAsAProtocolMismatchCandidate()
    {
        var agent = new DowngradableAgent(this);
        await agent.StartAsync(v2: false);

        var failure = await Should.ThrowAsync<Exception>(() => _factory.CreateScriptServiceV2Client(agent.Endpoint).GetStatusAsync(new ScriptStatusRequestV2(new ScriptTicket("nope"), 0))).WaitAsync(Wait);

        failure.GetType().ShouldBe(typeof(HalibutClientException), "pinned: this is the plain transport exception the classifier treats as transient");
        (Squid.Core.Halibut.Resilience.TransientFailureClassifier.IsTransient(failure) || Squid.Core.Halibut.Resilience.TransientFailureClassifier.IsServiceMissing(failure))
            .ShouldBeTrue($"a V1-only agent's answer to a V2 call ({failure.GetType().Name}: {failure.Message}) must be one the strategy recognises, or a downgraded agent pauses forever");
    }

    [Fact]
    public async Task ScriptStartedOverV2_ThenAgentDowngraded_ResumesOverV1_FinishesTheSameRun_NeverStartsASecond()
    {
        var agent = new DowngradableAgent(this);
        await agent.StartAsync(v2: true);
        var run = NewRun(agent);
        using var pause = new CancellationTokenSource();
        var request = Request(run, "sleep 5; echo finished-after-downgrade; exit 0");
        request.ShouldCancelRemoteScript = () => false;
        pause.CancelAfter(TimeSpan.FromSeconds(1.5));

        await Should.ThrowAsync<OperationCanceledException>(() => run.Strategy().ExecuteScriptAsync(request, pause.Token)).WaitAsync(Wait);
        run.Store.Version.ShouldBe(ScriptProtocolVersion.V2);

        await agent.DowngradeAsync(run.Machine);
        var resumed = await run.Strategy().ExecuteScriptAsync(Request(run, "sleep 5; echo finished-after-downgrade; exit 0"), CancellationToken.None).WaitAsync(Wait);

        resumed.Success.ShouldBeTrue("the script that was running when the agent was downgraded must run to completion and be reported");
        resumed.LogLines.ShouldContain("finished-after-downgrade");
        agent.V2.StartCalls.ShouldBe(1);
        agent.V1StartCalls.ShouldBe(0, "the ticket is still live in the agent's state: observe it, never start a second one");
        run.Store.HasPointer.ShouldBeFalse("the deployment is no longer parked on this ticket");
        run.Breakers.GetOrCreate(run.Machine.Id).ConsecutiveFailures.ShouldBe(0, "a protocol mismatch must not charge the machine's breaker");
        run.Cache.TryGet(run.Machine.Id).SupportsScriptServiceV2.ShouldBeFalse("the answer the agent gave replaced the stale entry");
    }

    [Fact]
    public async Task ScriptFinishedOverV2_ThenAgentDowngraded_ResumeFailsTheActionAsLost_NeverRerunsIt_AndDoesNotPause()
    {
        var agent = new DowngradableAgent(this);
        await agent.StartAsync(v2: true);
        var run = NewRun(agent);
        run.Store.SuppressClear = true;   // the server "crashed" after the agent released the ticket, before clearing its pointer

        var first = await run.Strategy().ExecuteScriptAsync(Request(run, "echo ran-once; exit 0"), CancellationToken.None).WaitAsync(Wait);
        first.Success.ShouldBeTrue();
        run.Store.HasPointer.ShouldBeTrue();

        run.Store.SuppressClear = false;
        await agent.DowngradeAsync(run.Machine);
        var resumed = await run.Strategy().ExecuteScriptAsync(Request(run, "echo ran-once; exit 0"), CancellationToken.None).WaitAsync(Wait);

        resumed.Success.ShouldBeFalse("a released V2 ticket cannot be told apart from a lost one over V1, so the outcome is unknown");
        resumed.StderrLines.ShouldHaveSingleItem().ShouldContain("did NOT run it again");
        agent.V2.StartCalls.ShouldBe(1);
        agent.V1StartCalls.ShouldBe(0, "the script may have run: it must never be dispatched again");
        run.Store.HasPointer.ShouldBeFalse("failing the action releases the pointer, so the next resume does not pause again");
        run.Breakers.GetOrCreate(run.Machine.Id).ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task StaleCacheAdvertisingV2_OnAnAlreadyDowngradedAgent_FreshDispatchFallsBackToV1_AndSucceeds()
    {
        var agent = new DowngradableAgent(this);
        await agent.StartAsync(v2: true);
        var run = NewRun(agent);
        await agent.DowngradeAsync(run.Machine);

        var result = await run.Strategy().ExecuteScriptAsync(Request(run, "echo hello-after-downgrade; exit 0"), CancellationToken.None).WaitAsync(Wait);

        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain("hello-after-downgrade");
        agent.V2.StartCalls.ShouldBe(0, "the V1-only host never served a V2 start");
        agent.V1StartCalls.ShouldBe(1);
        run.Store.RecordedVersions.ShouldBe(new[] { ScriptProtocolVersion.V2, ScriptProtocolVersion.V1 });   // the pointer is re-recorded in the dialect the script was actually started with
        run.Breakers.GetOrCreate(run.Machine.Id).ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task AgentThatIsStillV2_AndUnreachable_StillPauses_AndChargesTheBreaker_NotTreatedAsADowngrade()
    {
        var agent = new DowngradableAgent(this);
        await agent.StartAsync(v2: true);
        var run = NewRun(agent);
        await agent.StopAsync();

        await Should.ThrowAsync<Exception>(() => run.Strategy().ExecuteScriptAsync(Request(run, "echo never; exit 0"), CancellationToken.None)).WaitAsync(Wait);

        run.Cache.TryGet(run.Machine.Id).SupportsScriptServiceV2.ShouldBeTrue("an unanswered capabilities call proves nothing, so the cached advertisement stands");
        agent.V1StartCalls.ShouldBe(0);
        run.Breakers.GetOrCreate(run.Machine.Id).ConsecutiveFailures.ShouldBe(1);
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private Run NewRun(DowngradableAgent agent)
    {
        var machine = new Machine
        {
            Id = Interlocked.Increment(ref _machineSeed),
            Name = $"downgrade-e2e-{_machineSeed}",
            Endpoint = agent.EndpointJson
        };

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        // Exactly what the health check stored while the agent still spoke V2.
        cache.Store(machine.Id, agent.Capabilities.Metadata, agent.Capabilities.AgentVersion, agent.Capabilities.SupportedServices);

        return new Run(machine, cache, _factory);
    }

    private static ScriptExecutionRequest Request(Run run, string script) => new()
    {
        Machine = run.Machine,
        ScriptBody = script,
        ExecutionMode = ExecutionMode.DirectScript,
        Syntax = ScriptSyntax.Bash,
        ReleaseVersion = "1.0.0",
        ServerTaskId = 4343,
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

    /// <summary>One real agent whose Halibut host can be swapped from a V2-serving one to a V1-only one over the same script state.</summary>
    private sealed class DowngradableAgent
    {
        private readonly ScriptServiceDowngradeE2ETests _test;
        private readonly LocalScriptService _local = new(new ScriptStateStoreFactory());
        private readonly CountingV1 _v1;
        private TentacleHalibutHost _host = null!;
        private X509Certificate2 _cert = null!;

        public DowngradableAgent(ScriptServiceDowngradeE2ETests test)
        {
            _test = test;
            _v1 = new CountingV1(new BackendScriptServiceAdapter(_local));
            V2 = new CountingV2(_local);
        }

        public CountingV2 V2 { get; }
        public int V1StartCalls => _v1.StartCalls;
        public ServiceEndPoint Endpoint { get; private set; } = null!;
        public CapabilitiesResponse Capabilities { get; private set; } = null!;
        public string EndpointJson => $$"""{"CommunicationStyle":"TentacleListening","Uri":"{{Endpoint.BaseUri}}","Thumbprint":"{{_cert.Thumbprint}}"}""";

        public async Task StartAsync(bool v2)
        {
            _cert = NewCert("CN=downgrade-e2e-agent");
            IScriptService service = v2 ? new BackendScriptServiceV2Adapter(_local, V2) : _v1;

            _host = new TentacleHalibutHost(_cert, service, new TentacleSettings(), new CapabilitiesService(metadata: null, advertiseScriptServiceV2: service is IScriptServiceV2));
            _host.StartListening(0, _test._serverCert.Thumbprint);
            _test._disposables.Add(_host);

            Endpoint = new ServiceEndPoint(new Uri($"https://localhost:{_host.ListeningPort}/"), _cert.Thumbprint, _test._limits);
            Capabilities = await _test._factory.CreateCapabilitiesClient(Endpoint).GetCapabilitiesAsync(new CapabilitiesRequest()).WaitAsync(Wait);
        }

        public async Task StopAsync() => await _host.DisposeAsync();

        /// <summary>Replaces the V2-serving host with a V1-only one (new port, same script state) and points the machine at it.</summary>
        public async Task DowngradeAsync(Machine machine)
        {
            await _host.DisposeAsync();
            await StartAsync(v2: false);

            machine.Endpoint = EndpointJson;
        }
    }

    private sealed class Run
    {
        private readonly HalibutClientFactory _factory;

        public Run(Machine machine, InMemoryMachineRuntimeCapabilitiesCache cache, HalibutClientFactory factory)
        {
            Machine = machine;
            Cache = cache;
            _factory = factory;
            Store = new InMemoryStore();
            Breakers = new MachineCircuitBreakerRegistry(new HalibutSetting());
        }

        public Machine Machine { get; }
        public InMemoryMachineRuntimeCapabilitiesCache Cache { get; }
        public InMemoryStore Store { get; }
        public MachineCircuitBreakerRegistry Breakers { get; }

        public HalibutMachineExecutionStrategy Strategy()
        {
            var setting = new HalibutSetting { RpcRetry = new RpcRetrySettings { BudgetSeconds = 2, CancelBudgetSeconds = 2, CompleteBudgetSeconds = 2 } };
            var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 100, MaxPollIntervalMs = 500 });
            var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(_factory), new V2ScriptDispatcher(_factory) }, Cache);
            var store = new DirectStoreAccess(Substitute.For<IMachineRuntimeCapabilitiesPersistence>());
            var refresher = new MachineCapabilitiesRefresher(_factory, Cache, store, new MachineCapabilitiesRecorder(Cache, store), setting);

            return new HalibutMachineExecutionStrategy(_factory, new CalamariPayloadBuilder(Substitute.For<IYamlNuGetPacker>()), observer, setting, Breakers, inFlightStore: Store, dispatcherSelector: selector, capabilitiesRefresher: refresher);
        }
    }

    /// <summary>The shared persisted-capabilities store is covered by the integration tier; here it is a stand-in that stores nothing.</summary>
    private sealed class DirectStoreAccess : IMachineCapabilitiesStoreAccess
    {
        private readonly IMachineRuntimeCapabilitiesPersistence _persistence;

        public DirectStoreAccess(IMachineRuntimeCapabilitiesPersistence persistence) => _persistence = persistence;

        public Task<T> RunAsync<T>(Func<IMachineRuntimeCapabilitiesPersistence, Task<T>> operation) => operation(_persistence);
    }

    private sealed class InMemoryStore : IInFlightScriptStore
    {
        private (string Ticket, ScriptProtocolVersion Version)? _record;

        public bool SuppressClear { get; set; }
        public bool HasPointer => _record != null;
        public ScriptProtocolVersion? Version => _record?.Version;
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
            if (!SuppressClear) _record = null;

            return Task.CompletedTask;
        }

        public Task<string> TryGetTicketAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default) => Task.FromResult(_record?.Ticket);

        public Task<ScriptProtocolVersion?> TryGetProtocolAsync(int serverTaskId, DispatchSlot slot, CancellationToken cancellationToken = default) => Task.FromResult<ScriptProtocolVersion?>(_record?.Version);

        public Task<bool> IsMachineBusyAsync(int machineId, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class CountingV1 : IScriptService
    {
        private readonly IScriptService _inner;
        private int _startCalls;

        public CountingV1(IScriptService inner) => _inner = inner;

        public int StartCalls => _startCalls;

        public ScriptStatusResponse StartScript(StartScriptCommand command)
        {
            Interlocked.Increment(ref _startCalls);

            return _inner.StartScript(command);
        }

        public ScriptStatusResponse GetStatus(ScriptStatusRequest request) => _inner.GetStatus(request);

        public ScriptStatusResponse CompleteScript(CompleteScriptCommand command) => _inner.CompleteScript(command);

        public ScriptStatusResponse CancelScript(CancelScriptCommand command) => _inner.CancelScript(command);
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
