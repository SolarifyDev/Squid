using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halibut;
using Halibut.Diagnostics;
using NSubstitute;
using Shouldly;
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
/// Fidelity tier: HIGH. The REAL <see cref="HalibutMachineExecutionStrategy"/> with the real selector,
/// dispatchers, observer and <see cref="HalibutClientFactory"/> talks over a real Halibut listening
/// connection (loopback mTLS) to a REAL <see cref="TentacleHalibutHost"/> wrapping the real
/// <see cref="LocalScriptService"/> that runs real bash. The capability the selector reads is the one the
/// real agent advertises through the real capabilities service (stored exactly as the health check stores it).
/// Only the checkpoint store is in-memory (the persistence tier is covered by the integration tests).
///
/// <para>One test is MEDIUM-MOCK for the agent only: POSIX exit statuses are 0-255, so a script cannot exit -1
/// on Linux/macOS (Windows can: <c>exit /b -1</c>). That test wraps the agent's V2 service so a real script's
/// exit 255 is reported as -1, to prove the server treats a real -1 over V2 as an ordinary completion.</para>
///
/// <para>Diagnosing a failure: every wait carries an explicit timeout; the message names the signal. To inspect
/// the agent manually, look in the temp dir for <c>squid-tentacle-{ticket}</c> workspaces and the tombstone store.</para>
/// </summary>
[Trait("Category", "E2E")]
public sealed class ScriptServiceV2DispatchE2ETests : IAsyncLifetime
{
    private static int _machineSeed = 91000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(60);

    private readonly HalibutTimeoutsAndLimits _limits = HalibutTimeoutsAndLimits.RecommendedValues();
    private readonly List<IAsyncDisposable> _disposables = new();
    private X509Certificate2 _serverCert = null!;
    private HalibutRuntime _server = null!;
    private CountingFactory _factory = null!;

    public Task InitializeAsync()
    {
        _serverCert = NewCert("CN=v2-e2e-server");
        _server = new HalibutRuntimeBuilder().WithServerCertificate(_serverCert).WithHalibutTimeoutsAndLimits(_limits).Build();
        _factory = new CountingFactory(new HalibutClientFactory(_server));

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var disposable in _disposables) await disposable.DisposeAsync();

        await _server.DisposeAsync();
    }

    [Fact]
    public async Task AgentAdvertisingV2_IsDispatchedOverV2_AndTheRealExitCodeAndOutputComeBack()
    {
        var agent = await StartAgentAsync(v2: true);
        var run = await NewRunAsync(agent);

        var result = await run.Strategy().ExecuteScriptAsync(Request(run, "echo hello-over-v2; exit 5"), CancellationToken.None).WaitAsync(Wait);

        agent.SupportedServices.ShouldContain(ScriptServiceCapabilities.V2, "the real agent must advertise V2 for the selector to choose it");
        result.ExitCode.ShouldBe(5, "a real script's exit code must survive the V2 round trip");
        result.LogLines.ShouldContain("hello-over-v2");
        _factory.V2Opened.ShouldBeGreaterThan(0, "an agent advertising V2 must be dispatched over V2");
        _factory.V1Opened.ShouldBe(0, "no V1 client may be opened for a V2 dispatch");
        agent.V2.StartCalls.ShouldBe(1);
        run.Store.RecordedVersions.ShouldBe(new[] { ScriptProtocolVersion.V2 });
        (await ReleasedOnAgentAsync(agent, run.LastTicket)).ShouldBeTrue("the V2 Complete must have released the ticket (a second Complete answers AlreadyCompleted)");
    }

    [Fact]
    public async Task V1OnlyAgent_DoesNotAdvertiseV2_AndIsDispatchedOverV1()
    {
        var agent = await StartAgentAsync(v2: false);
        var run = await NewRunAsync(agent);

        var result = await run.Strategy().ExecuteScriptAsync(Request(run, "echo hello-over-v1; exit 0"), CancellationToken.None).WaitAsync(Wait);

        agent.SupportedServices.ShouldNotContain(ScriptServiceCapabilities.V2);
        result.Success.ShouldBeTrue();
        result.LogLines.ShouldContain("hello-over-v1");
        _factory.V1Opened.ShouldBeGreaterThan(0);
        _factory.V2Opened.ShouldBe(0, "a V1-only agent must never be sent a V2 call: it answers with a plain transport exception");
        run.Store.RecordedVersions.ShouldBe(new[] { ScriptProtocolVersion.V1 });
    }

    [Fact]
    public async Task ColdCache_IsDispatchedOverV1_EvenWhenTheAgentWouldHaveSpokenV2()
    {
        var agent = await StartAgentAsync(v2: true);
        var run = await NewRunAsync(agent, primeCapabilities: false);

        var result = await run.Strategy().ExecuteScriptAsync(Request(run, "echo cold; exit 0"), CancellationToken.None).WaitAsync(Wait);

        result.Success.ShouldBeTrue();
        _factory.V2Opened.ShouldBe(0, "before the first health check lands nothing is advertised, so V1 — which every agent speaks — is used");
        agent.V2.StartCalls.ShouldBe(0);
    }

    [Fact]
    public async Task ScriptExitingMinusOne_OverV2_IsAnOrdinaryCompletion_AndAResumeNeverRedispatchesIt()
    {
        // MEDIUM-MOCK for the agent: exit 255 is reported as -1 (what a Windows script can really exit with).
        var agent = await StartAgentAsync(v2: true, rewriteExit255ToMinusOne: true);
        var run = await NewRunAsync(agent);
        run.Store.SuppressClear = true;   // the server "crashed" after the agent released the ticket, before clearing its pointer

        var first = await run.Strategy().ExecuteScriptAsync(Request(run, "exit 255"), CancellationToken.None).WaitAsync(Wait);

        first.ExitCode.ShouldBe(-1);
        first.Success.ShouldBeFalse();
        first.StderrLines.ShouldBeEmpty("-1 over V2 is a real exit code, not the lost-result condition");
        agent.V2.StartCalls.ShouldBe(1);
        run.Store.HasPointer.ShouldBeTrue("the pointer survived the simulated crash");

        run.Store.SuppressClear = false;
        var resumed = await run.Strategy().ExecuteScriptAsync(Request(run, "exit 255"), CancellationToken.None).WaitAsync(Wait);

        agent.V2.StartCalls.ShouldBe(1, "the agent remembers the ticket completed with -1: a resume must report that, never run the script again");
        resumed.ExitCode.ShouldBe(-1);
        resumed.Success.ShouldBeFalse();
        agent.V2.CancelCalls.ShouldBe(0, "the finished script is recognised from GetStatus alone (the agent answers a released ticket from its tombstone); a CancelScript would kill a live one");
    }

    [Fact]
    public async Task PausedScript_ResumesOverV2_ReattachingToTheSameRunningTicket()
    {
        var agent = await StartAgentAsync(v2: true);
        var run = await NewRunAsync(agent);
        using var pause = new CancellationTokenSource();

        // The first dispatch is interrupted as soon as the script is running; the decision says "pausing", so the agent must keep running it.
        var firstRequest = Request(run, "sleep 4; echo finished-after-resume; exit 0");
        firstRequest.ShouldCancelRemoteScript = () => false;
        pause.CancelAfter(TimeSpan.FromSeconds(1));

        await Should.ThrowAsync<OperationCanceledException>(() => run.Strategy().ExecuteScriptAsync(firstRequest, pause.Token)).WaitAsync(Wait);

        var ticket = run.Store.Ticket;
        ticket.ShouldNotBeNullOrEmpty("the pointer must be recorded before the pause");
        run.Store.Version.ShouldBe(ScriptProtocolVersion.V2);

        var resumed = await run.Strategy().ExecuteScriptAsync(Request(run, "sleep 4; echo finished-after-resume; exit 0"), CancellationToken.None).WaitAsync(Wait);

        resumed.Success.ShouldBeTrue("the re-attached script must run to completion");
        resumed.LogLines.ShouldContain("finished-after-resume");
        agent.V2.StartCalls.ShouldBe(1, "resume must re-attach to the running script, never start a second one");
        run.RecordedTickets.Distinct().Count().ShouldBe(1, "the same ticket is used before and after the pause");
    }

    // ── harness ──────────────────────────────────────────────────────────────

    private async Task<Agent> StartAgentAsync(bool v2, bool rewriteExit255ToMinusOne = false)
    {
        var cert = NewCert("CN=v2-e2e-agent");
        var local = new LocalScriptService(new ScriptStateStoreFactory());
        var instrumented = new InstrumentedV2(local, rewriteExit255ToMinusOne);
        IScriptService scriptService = v2 ? new BackendScriptServiceV2Adapter(local, instrumented) : new BackendScriptServiceAdapter(local);

        var caps = new CapabilitiesService(metadata: null, advertiseScriptServiceV2: scriptService is IScriptServiceV2);
        var host = new TentacleHalibutHost(cert, scriptService, new TentacleSettings(), caps);
        host.StartListening(0, _serverCert.Thumbprint);
        _disposables.Add(host);

        var endpoint = new ServiceEndPoint(new Uri($"https://localhost:{host.ListeningPort}/"), cert.Thumbprint, _limits);
        var response = await _factory.Inner.CreateCapabilitiesClient(endpoint).GetCapabilitiesAsync(new CapabilitiesRequest()).WaitAsync(Wait);

        return new Agent(host.ListeningPort, cert.Thumbprint, endpoint, instrumented, response);
    }

    private async Task<Run> NewRunAsync(Agent agent, bool primeCapabilities = true)
    {
        var machine = new Machine
        {
            Id = Interlocked.Increment(ref _machineSeed),
            Name = $"v2-e2e-{_machineSeed}",
            Endpoint = $$"""{"CommunicationStyle":"TentacleListening","Uri":"https://localhost:{{agent.Port}}/","Thumbprint":"{{agent.Thumbprint}}"}"""
        };

        var cache = new InMemoryMachineRuntimeCapabilitiesCache();

        // Exactly what the health check does with the agent's capabilities response.
        if (primeCapabilities) cache.Store(machine.Id, agent.Capabilities.Metadata, agent.Capabilities.AgentVersion, agent.Capabilities.SupportedServices);

        await Task.CompletedTask;

        return new Run(machine, cache, _factory);
    }

    private static ScriptExecutionRequest Request(Run run, string script) => new()
    {
        Machine = run.Machine,
        ScriptBody = script,
        ExecutionMode = ExecutionMode.DirectScript,
        Syntax = ScriptSyntax.Bash,
        ReleaseVersion = "1.0.0",
        ServerTaskId = 4242,
        StepId = 11,
        ActionId = 22,
        StepName = "step",
        ActionName = "action",
        Timeout = TimeSpan.FromSeconds(50),
        Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
    };

    private async Task<bool> ReleasedOnAgentAsync(Agent agent, string ticket)
    {
        var client = _factory.Inner.CreateScriptServiceV2Client(agent.Endpoint);
        var again = await client.CompleteScriptAsync(new CompleteScriptCommandV2(new ScriptTicket(ticket), 0)).WaitAsync(Wait);

        return again.Outcome == CompleteScriptOutcomeV2.AlreadyCompleted;
    }

    private static X509Certificate2 NewCert(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    private sealed record Agent(int Port, string Thumbprint, ServiceEndPoint Endpoint, InstrumentedV2 V2, CapabilitiesResponse Capabilities)
    {
        public IReadOnlyList<string> SupportedServices => Capabilities.SupportedServices;
    }

    private sealed class Run
    {
        private readonly InMemoryMachineRuntimeCapabilitiesCache _cache;
        private readonly CountingFactory _factory;

        public Run(Machine machine, InMemoryMachineRuntimeCapabilitiesCache cache, CountingFactory factory)
        {
            Machine = machine;
            _cache = cache;
            _factory = factory;
            Store = new InMemoryStore();
        }

        public Machine Machine { get; }
        public InMemoryStore Store { get; }
        public List<string> RecordedTickets => Store.AllRecordedTickets;
        public string LastTicket => Store.AllRecordedTickets.Last();

        public HalibutMachineExecutionStrategy Strategy()
        {
            var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 100, MaxPollIntervalMs = 500 });
            var selector = new ScriptDispatcherSelector(new IScriptDispatcher[] { new V1ScriptDispatcher(_factory), new V2ScriptDispatcher(_factory) }, _cache);

            return new HalibutMachineExecutionStrategy(_factory, new CalamariPayloadBuilder(Substitute.For<IYamlNuGetPacker>()), observer, new HalibutSetting(), inFlightStore: Store, dispatcherSelector: selector);
        }
    }

    /// <summary>Counts which protocol's client the server opened, delegating to the real Halibut factory.</summary>
    private sealed class CountingFactory : IHalibutClientFactory, IScriptServiceV2ClientFactory
    {
        public CountingFactory(HalibutClientFactory inner) => Inner = inner;

        public HalibutClientFactory Inner { get; }
        public int V1Opened { get; private set; }
        public int V2Opened { get; private set; }

        public IAsyncScriptService CreateClient(ServiceEndPoint endpoint)
        {
            V1Opened++;
            return Inner.CreateClient(endpoint);
        }

        public IAsyncScriptServiceV2 CreateScriptServiceV2Client(ServiceEndPoint endpoint)
        {
            V2Opened++;
            return Inner.CreateScriptServiceV2Client(endpoint);
        }

        public IAsyncCapabilitiesService CreateCapabilitiesClient(ServiceEndPoint endpoint) => Inner.CreateCapabilitiesClient(endpoint);

        public IAsyncClientFileTransferService CreateFileTransferClient(ServiceEndPoint endpoint) => Inner.CreateFileTransferClient(endpoint);
    }

    private sealed class InMemoryStore : IInFlightScriptStore
    {
        private (string Ticket, ScriptProtocolVersion Version)? _record;

        public bool SuppressClear { get; set; }
        public bool HasPointer => _record != null;
        public string Ticket => _record?.Ticket;
        public ScriptProtocolVersion? Version => _record?.Version;
        public List<string> AllRecordedTickets { get; } = new();
        public List<ScriptProtocolVersion> RecordedVersions { get; } = new();

        public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, CancellationToken cancellationToken = default)
            => RecordDispatchedAsync(serverTaskId, slot, scriptTicket, ScriptProtocolVersion.V1, cancellationToken);

        public Task RecordDispatchedAsync(int serverTaskId, DispatchSlot slot, string scriptTicket, ScriptProtocolVersion protocol, CancellationToken cancellationToken = default)
        {
            _record = (scriptTicket, protocol);
            AllRecordedTickets.Add(scriptTicket);
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

    /// <summary>The agent's V2 service as the real host serves it: delegates to the real <see cref="LocalScriptService"/>, counting StartScript calls and optionally re-labelling a real exit 255 as -1.</summary>
    private sealed class InstrumentedV2 : IScriptServiceV2
    {
        private readonly IScriptServiceV2 _inner;
        private readonly bool _exit255IsMinusOne;
        private int _startCalls;
        private int _cancelCalls;

        public InstrumentedV2(IScriptServiceV2 inner, bool exit255IsMinusOne)
        {
            _inner = inner;
            _exit255IsMinusOne = exit255IsMinusOne;
        }

        public int StartCalls => _startCalls;
        public int CancelCalls => _cancelCalls;

        public ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command)
        {
            Interlocked.Increment(ref _startCalls);

            return Map(_inner.StartScript(command));
        }

        public ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request) => Map(_inner.GetStatus(request));

        public ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command)
        {
            Interlocked.Increment(ref _cancelCalls);

            return Map(_inner.CancelScript(command));
        }

        public CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command)
        {
            var response = _inner.CompleteScript(command);

            return response.FinalStatus == null ? response : new CompleteScriptResponseV2(response.Ticket, response.Outcome, Map(response.FinalStatus));
        }

        private ScriptStatusResponseV2 Map(ScriptStatusResponseV2 status)
            => _exit255IsMinusOne && status.State == ScriptStateV2.Completed && status.ExitCode == 255
                ? new ScriptStatusResponseV2(status.Ticket, status.State, -1, status.Logs, status.NextLogSequence)
                : status;
    }
}
