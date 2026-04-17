using Halibut;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.UnitTests.Halibut.Resilience;

public sealed class LivenessObserverIntegrationTests
{
    [Fact]
    public async Task Observer_LivenessProbeFails_ThresholdReached_ThrowsAgentUnreachable_QuickAbort()
    {
        // Probe returns false every time → threshold=2 should trip after ~2 × 100ms probes.
        var probe = new FakeLivenessProbe(alive: false);
        var liveness = new LivenessSettings
        {
            ProbeIntervalSeconds = 1,     // minimum allowed
            ProbeTimeoutSeconds = 1,
            FailureThreshold = 2
        };
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 50, MaxPollIntervalMs = 100 }, liveness, probe);

        var scriptClient = new FakeNeverCompletingScriptClient();
        var endpoint = new ServiceEndPoint(new Uri("poll://fake/"), "AABBCCDD", HalibutTimeoutsAndLimitsFactory());

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Should.ThrowAsync<AgentUnreachableException>(async () =>
            await observer.ObserveAndCompleteAsync(
                new Machine { Name = "test-agent" },
                scriptClient,
                new ScriptTicket("t"),
                TimeSpan.FromMinutes(30),   // far longer than probe threshold
                CancellationToken.None,
                masker: null,
                initialStartResponse: null,
                endpoint: endpoint));
        sw.Stop();

        ex.MachineName.ShouldBe("test-agent");
        ex.ConsecutiveFailures.ShouldBeGreaterThanOrEqualTo(2);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15),
            "liveness probe must abort the wait well under the 30-minute script timeout");
    }

    [Fact]
    public async Task Observer_LivenessProbePasses_ScriptCompletesNormally()
    {
        var probe = new FakeLivenessProbe(alive: true);
        var liveness = new LivenessSettings
        {
            ProbeIntervalSeconds = 1,
            ProbeTimeoutSeconds = 1,
            FailureThreshold = 2
        };
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 50, MaxPollIntervalMs = 100 }, liveness, probe);

        var scriptClient = new FakeFastCompletingScriptClient();
        var endpoint = new ServiceEndPoint(new Uri("poll://fake/"), "AABBCCDD", HalibutTimeoutsAndLimitsFactory());

        var result = await observer.ObserveAndCompleteAsync(
            new Machine { Name = "healthy-agent" },
            scriptClient,
            new ScriptTicket("t"),
            TimeSpan.FromMinutes(5),
            CancellationToken.None,
            masker: null,
            initialStartResponse: null,
            endpoint: endpoint);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task Observer_NullProbe_SkipsLiveness_ScriptCompletesNormally()
    {
        // Back-compat: callers that don't wire a probe (dev/test) must still work.
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 50, MaxPollIntervalMs = 100 });

        var result = await observer.ObserveAndCompleteAsync(
            new Machine { Name = "no-probe-agent" },
            new FakeFastCompletingScriptClient(),
            new ScriptTicket("t"),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);

        result.Success.ShouldBeTrue();
    }

    private static global::Halibut.Diagnostics.HalibutTimeoutsAndLimits HalibutTimeoutsAndLimitsFactory()
        => global::Halibut.Diagnostics.HalibutTimeoutsAndLimits.RecommendedValues();

    private sealed class FakeLivenessProbe : IAgentLivenessProbe
    {
        private readonly bool _alive;
        public FakeLivenessProbe(bool alive) => _alive = alive;
        public Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct) => Task.FromResult(_alive);
    }

    private sealed class FakeNeverCompletingScriptClient : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) =>
            Task.FromResult(new ScriptStatusResponse(command.ScriptTicket, ProcessState.Running, 0, new List<ProcessOutput>(), 0));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) =>
            Task.FromResult(new ScriptStatusResponse(request.Ticket, ProcessState.Running, 0, new List<ProcessOutput>(), request.LastLogSequence));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) =>
            Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), command.LastLogSequence));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) =>
            Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, -1, new List<ProcessOutput>(), command.LastLogSequence));
    }

    private sealed class FakeFastCompletingScriptClient : IAsyncScriptService
    {
        public Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command) =>
            Task.FromResult(new ScriptStatusResponse(command.ScriptTicket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        public Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request) =>
            Task.FromResult(new ScriptStatusResponse(request.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        public Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command) =>
            Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, 0, new List<ProcessOutput>(), command.LastLogSequence));

        public Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command) =>
            Task.FromResult(new ScriptStatusResponse(command.Ticket, ProcessState.Complete, -1, new List<ProcessOutput>(), command.LastLogSequence));
    }
}
