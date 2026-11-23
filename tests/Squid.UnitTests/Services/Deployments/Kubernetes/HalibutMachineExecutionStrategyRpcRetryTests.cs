using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Halibut;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Common;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Lifecycle;
using Squid.Core.Services.DeploymentExecution.Packages;
using Squid.Core.Services.DeploymentExecution.Script;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Pins the seam between <see cref="HalibutMachineExecutionStrategy"/> and the RPC retry layer: the
/// strategy must hand the observer a retrying client (delete that call site and a connection blip
/// pauses the deployment again), and however many attempts a dispatch burns, the machine's circuit
/// breaker sees at most ONE failure for it.
/// </summary>
[Collection("HalibutPollingWorkAdmissionStaticState")]
public class HalibutMachineExecutionStrategyRpcRetryTests
{
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Mock<IHalibutClientFactory> _factory = new();
    private readonly MachineCircuitBreakerRegistry _breakers = new(new HalibutSetting());
    private readonly Machine _machine = new() { Id = 4242, Name = "agent", Endpoint = """{"CommunicationStyle":"KubernetesAgent","SubscriptionId":"sub-retry","Thumbprint":"AABBCCDD"}""" };

    private HalibutMachineExecutionStrategy Strategy(IAsyncScriptService agent, int budgetSeconds = 120)
    {
        _factory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>())).Returns(agent);

        var retry = new RpcRetryHandler(TimeSpan.FromSeconds(budgetSeconds), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), () => _now, (d, _) => { _now += d; return Task.CompletedTask; });
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 });

        return new HalibutMachineExecutionStrategy(_factory.Object, new CalamariPayloadBuilder(new Mock<IYamlNuGetPacker>().Object), observer, new HalibutSetting(), _breakers, rpcRetry: retry);
    }

    private ScriptExecutionRequest Request() => new()
    {
        Machine = _machine,
        ScriptBody = "echo hi",
        ExecutionMode = ExecutionMode.DirectScript,
        ReleaseVersion = "1.0.0",
        Variables = new List<Message.Models.Deployments.Variable.VariableDto>()
    };

    [Fact]
    public async Task BlipDuringScript_DeploymentSucceeds_AndBreakerSeesNoFailure()
    {
        var agent = new FlakyAgent(getStatusFailuresBeforeRecovery: 3);

        var result = await Strategy(agent).ExecuteScriptAsync(Request(), CancellationToken.None);

        result.Success.ShouldBeTrue();
        agent.GetStatusCalls.ShouldBe(3 + 2, "three failed attempts, then the running and complete polls");
        _breakers.GetOrCreate(_machine.Id).ConsecutiveFailures.ShouldBe(0);
    }

    [Fact]
    public async Task OutageBeyondBudget_ThrowsOriginal_AndBreakerCountsExactlyOneFailureForTheDispatch()
    {
        var agent = new FlakyAgent(getStatusFailuresBeforeRecovery: int.MaxValue);

        await Should.ThrowAsync<HalibutClientException>(() => Strategy(agent, budgetSeconds: 10).ExecuteScriptAsync(Request(), CancellationToken.None));

        agent.GetStatusCalls.ShouldBeGreaterThan(2, "it retried");
        _breakers.GetOrCreate(_machine.Id).ConsecutiveFailures.ShouldBe(1, "one dispatch = one breaker failure, not one per attempt");
    }

    [Fact]
    public async Task WithoutAnExplicitHandler_TheStrategyUsesTheHalibutSettingRpcRetryBudget()
    {
        // Wiring seam: HalibutSetting.RpcRetry must reach the handler the strategy builds. With the 120s
        // default this outage would retry for minutes; the configured 1s budget must end it quickly.
        var agent = new FlakyAgent(getStatusFailuresBeforeRecovery: int.MaxValue);
        _factory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>())).Returns(agent);
        var setting = new HalibutSetting { RpcRetry = new RpcRetrySettings { BudgetSeconds = 1 } };
        var strategy = new HalibutMachineExecutionStrategy(_factory.Object, new CalamariPayloadBuilder(new Mock<IYamlNuGetPacker>().Object), new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 1, MaxPollIntervalMs = 2 }), setting);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<HalibutClientException>(() => strategy.ExecuteScriptAsync(Request(), CancellationToken.None));

        watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20), "the configured 1s budget, not the 120s default, must bound the retries");
        agent.GetStatusCalls.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task ObserverReceivesARetryingClient()
    {
        var observer = new Mock<IHalibutScriptObserver>();
        IAsyncScriptService seen = null;
        observer.Setup(o => o.ObserveAndCompleteAsync(It.IsAny<Machine>(), It.IsAny<IAsyncScriptService>(), It.IsAny<ScriptTicket>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>(), It.IsAny<SensitiveValueMasker>(), It.IsAny<ScriptStatusResponse>(), It.IsAny<ServiceEndPoint>(), It.IsAny<ScriptOutputSink>(), It.IsAny<RemoteScriptCancelDecision>()))
            .Callback<Machine, IAsyncScriptService, ScriptTicket, TimeSpan, CancellationToken, SensitiveValueMasker, ScriptStatusResponse, ServiceEndPoint, ScriptOutputSink, RemoteScriptCancelDecision>((_, c, _, _, _, _, _, _, _, _) => seen = c)
            .ReturnsAsync(new ScriptExecutionResult { Success = true });
        _factory.Setup(f => f.CreateClient(It.IsAny<ServiceEndPoint>())).Returns(new FlakyAgent(0));
        var strategy = new HalibutMachineExecutionStrategy(_factory.Object, new CalamariPayloadBuilder(new Mock<IYamlNuGetPacker>().Object), observer.Object, new HalibutSetting());

        await strategy.ExecuteScriptAsync(Request(), CancellationToken.None);

        seen.ShouldBeOfType<RetryingScriptClient>();
    }

    private sealed class FlakyAgent : IAsyncScriptService
    {
        private readonly int _failures;
        private int _status;

        public FlakyAgent(int getStatusFailuresBeforeRecovery) => _failures = getStatusFailuresBeforeRecovery;

        public int GetStatusCalls => _status;

        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command)
            => Task.FromResult(new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request)
        {
            var n = Interlocked.Increment(ref _status);

            if (n <= _failures) throw new HalibutClientException("connection reset");

            var state = n - _failures >= 2 ? ProcessState.Complete : ProcessState.Running;

            return Task.FromResult(new ScriptStatusResponse(request.Ticket, state, 0, new List<ProcessOutput>(), 1));
        }

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command)
            => Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 1));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command)
            => Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, -2, new List<ProcessOutput>(), 1));
    }
}
