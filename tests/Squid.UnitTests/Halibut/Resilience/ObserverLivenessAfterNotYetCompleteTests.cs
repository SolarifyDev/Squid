using Halibut;
using Shouldly;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.UnitTests.Halibut.Resilience;

/// <summary>
/// The observer stops probing the agent's liveness once it sees the script finish, because it then goes on to Complete. When
/// the agent answers Complete with "not yet complete" (V2) observation CONTINUES — and the liveness probe must too, or an
/// agent that has gone away is only noticed by the retry budgets or the script timeout instead of the quick liveness abort.
/// </summary>
public sealed class ObserverLivenessAfterNotYetCompleteTests
{
    private static readonly LivenessSettings Fast = new() { ProbeIntervalSeconds = 1, ProbeTimeoutSeconds = 1, FailureThreshold = 2 };
    private static readonly ServiceEndPoint Endpoint = new(new Uri("poll://fake/"), "AABBCCDD", global::Halibut.Diagnostics.HalibutTimeoutsAndLimits.RecommendedValues());

    private readonly ScriptTicket _ticket = new("t");

    [Fact]
    public async Task AfterNotYetComplete_AnAgentThatStopsAnswering_IsDetectedByTheLivenessProbe_NotByTheScriptTimeout()
    {
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 20, MaxPollIntervalMs = 50 }, Fast, new Probe(alive: false));
        var client = new CompleteSaysNotYetThenStaysRunning(_ticket);

        var started = DateTime.UtcNow;
        var ex = await Should.ThrowAsync<AgentUnreachableException>(() => observer.ObserveAgentScriptAsync(new Machine { Name = "agent" }, client, _ticket, TimeSpan.FromSeconds(12), CancellationToken.None, null, null, Endpoint, null, null));

        ex.ConsecutiveFailures.ShouldBeGreaterThanOrEqualTo(2);
        (DateTime.UtcNow - started).ShouldBeLessThan(TimeSpan.FromSeconds(10), "the liveness abort (a few probes), not the script timeout (12s here)");
        client.CompleteCalls.ShouldBe(1);
    }

    [Fact]
    public async Task AfterNotYetComplete_AHealthyAgent_StillCompletesNormally_AndTheRestartedProbeIsStopped()
    {
        var probe = new Probe(alive: true);
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 20, MaxPollIntervalMs = 50 }, Fast, probe);
        var client = new CompleteSaysNotYetThenFinishes(_ticket);

        var result = await observer.ObserveAgentScriptAsync(new Machine { Name = "agent" }, client, _ticket, TimeSpan.FromMinutes(5), CancellationToken.None, null, null, Endpoint, null, null);

        result.Success.ShouldBeTrue();
        client.CompleteCalls.ShouldBe(2);

        var probesAtEnd = probe.Calls;
        await Task.Delay(TimeSpan.FromMilliseconds(2500));

        probe.Calls.ShouldBe(probesAtEnd, "no probe loop may outlive the observation");
    }

    [Fact]
    public async Task AfterNotYetComplete_NoProbeConfigured_ObservationIsUnchanged()
    {
        var observer = new HalibutScriptObserver(new ObserverSettings { InitialPollIntervalMs = 5, MaxPollIntervalMs = 10 });

        var result = await observer.ObserveAgentScriptAsync(new Machine { Name = "agent" }, new CompleteSaysNotYetThenFinishes(_ticket), _ticket, TimeSpan.FromMinutes(5), CancellationToken.None, null, null, null, null, null);

        result.Success.ShouldBeTrue();
    }

    private sealed class Probe : IAgentLivenessProbe
    {
        private readonly bool _alive;
        private int _calls;

        public Probe(bool alive) => _alive = alive;

        public int Calls => Volatile.Read(ref _calls);

        public Task<bool> ProbeAsync(ServiceEndPoint endpoint, TimeSpan timeout, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);

            return Task.FromResult(_alive);
        }
    }

    /// <summary>GetStatus: Completed at once. Complete: "not yet complete" with a final status that is Running, every time.</summary>
    private sealed class CompleteSaysNotYetThenStaysRunning : IScriptAgentClient
    {
        private readonly ScriptTicket _ticket;

        public CompleteSaysNotYetThenStaysRunning(ScriptTicket ticket) => _ticket = ticket;

        private int _polls;

        public int CompleteCalls { get; private set; }

        public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
            => Task.FromResult(new AgentScriptStatus(_ticket, ++_polls == 1 ? AgentScriptState.Completed : AgentScriptState.Running, 0, new List<ProcessOutput>(), lastLogSequence));

        public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence) => Task.FromResult(new AgentScriptStatus(_ticket, AgentScriptState.Completed, -1, new List<ProcessOutput>(), lastLogSequence));

        public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
        {
            CompleteCalls++;

            return Task.FromResult(new AgentScriptCompletion(AgentCompleteOutcome.NotYetComplete, new AgentScriptStatus(_ticket, AgentScriptState.Running, 0, new List<ProcessOutput>(), lastLogSequence)));
        }
    }

    /// <summary>The first GetStatus says Completed, the first Complete says "not yet complete" (still Running), then everything finishes.</summary>
    private sealed class CompleteSaysNotYetThenFinishes : IScriptAgentClient
    {
        private readonly ScriptTicket _ticket;
        private int _status;

        public CompleteSaysNotYetThenFinishes(ScriptTicket ticket) => _ticket = ticket;

        public int CompleteCalls { get; private set; }

        public Task<AgentScriptStatus> GetStatusAsync(ScriptTicket ticket, long lastLogSequence)
            => Task.FromResult(new AgentScriptStatus(_ticket, AgentScriptState.Completed, 0, new List<ProcessOutput>(), lastLogSequence));

        public Task<AgentScriptStatus> CancelAsync(ScriptTicket ticket, long lastLogSequence) => throw new NotSupportedException();

        public Task<AgentScriptCompletion> CompleteAsync(ScriptTicket ticket, long lastLogSequence)
        {
            CompleteCalls++;
            _status++;

            return Task.FromResult(_status == 1
                ? new AgentScriptCompletion(AgentCompleteOutcome.NotYetComplete, new AgentScriptStatus(_ticket, AgentScriptState.Running, 0, new List<ProcessOutput>(), lastLogSequence))
                : new AgentScriptCompletion(AgentCompleteOutcome.Completed, new AgentScriptStatus(_ticket, AgentScriptState.Completed, 0, new List<ProcessOutput>(), lastLogSequence)));
        }
    }
}
