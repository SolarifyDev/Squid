using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Autofac;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

/// <summary>
/// The sweep's progress invariant, checked over a grid of configurations with the real service + coordinator on a fake clock:
/// every enabled machine that is due gets a probe STARTED within ⌈machines / MaxParallelism⌉ sweeps, whatever the sweep deadline, the probe timeout
/// (default, fitting, on the fit boundary, longer than the sweep), the time the due-list query takes, the time reloading each machine takes, and how many machines never answer. Along the way
/// no probe timer is ever shorter than the machine's full timeout, and a verdict is only ever recorded inside the sweep by a probe that either answered
/// or waited its full timeout: a probe the sweep deadline cut records nothing.
/// <para>Time moves only where the real sweep spends it: the due-list query advances the clock by the listing delay, reloading a machine before its probe by the setup delay, and a machine that never answers
/// holds its (single) worker until the test moves the clock to the instant its own timeout or the sweep deadline cancels it. MaxParallelism is 1, so a
/// hung probe means the whole sweep is waiting on the clock — the driver is deterministic, with no real-time sleeps.</para>
/// </summary>
public class SweepProgressInvariantTests
{
    private const int MachineCount = 5;
    private const int Parallelism = 1;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);

    public static IEnumerable<object[]> Grid()
    {
        foreach (var deadline in new[] { 30, 55, 300 })
        foreach (var timeout in new double?[] { null, deadline / 2.0, deadline - 6, deadline - 5, deadline * 2 })
        foreach (var listing in new[] { 0, 2, deadline / 2 })
        foreach (var setup in new[] { 0, 1 })
        foreach (var unreachable in new[] { 0, 1, 3 })
            yield return new object[] { deadline, timeout, listing, setup, unreachable };
    }

    [Theory]
    [MemberData(nameof(Grid))]
    public async Task EveryDueMachineIsStartedWithinABoundedNumberOfSweeps_NeverShortened_NoVerdictFromACutProbe(int deadlineSeconds, double? timeoutSeconds, int listingSeconds, int setupSeconds, int unreachable)
    {
        using var fleet = new SimulatedFleet(deadlineSeconds, timeoutSeconds, listingSeconds, setupSeconds, unreachable);
        var bound = (MachineCount + Parallelism - 1) / Parallelism;

        for (var sweep = 1; sweep <= bound && fleet.Started.Count < MachineCount; sweep++)
            await fleet.RunSweepAsync();

        fleet.Started.Count.ShouldBe(MachineCount, $"machines never started within {bound} sweeps: [{string.Join(",", fleet.NeverStarted)}]");
        fleet.Violations.ShouldBeEmpty();
        fleet.ArmedProbeTimers.ShouldAllBe(t => t == fleet.ExpectedTimeout, "a probe timer must always be the machine's full timeout");
    }

    /// <summary>A fleet of <see cref="MachineCount"/> KubernetesApi machines; the first <c>unreachable</c> ids never answer (they hang until cancelled).</summary>
    private sealed class SimulatedFleet : IDisposable
    {
        private readonly SweepClock _clock = new();
        private readonly List<Machine> _machines;
        private readonly HashSet<int> _unreachable;
        private readonly TimeSpan _deadline;
        private readonly TimeSpan _listing;
        private readonly TimeSpan _setup;
        private readonly HealthCheckTestHost _host;
        private readonly ConcurrentDictionary<int, DateTimeOffset> _probeStart = new();
        private readonly Channel<HungProbe> _hung = System.Threading.Channels.Channel.CreateUnbounded<HungProbe>();
        private DateTimeOffset _sweepDeadlineAt;

        public SimulatedFleet(int deadlineSeconds, double? timeoutSeconds, int listingSeconds, int setupSeconds, int unreachable)
        {
            _deadline = TimeSpan.FromSeconds(deadlineSeconds);
            _listing = TimeSpan.FromSeconds(listingSeconds);
            _setup = TimeSpan.FromSeconds(setupSeconds);
            _machines = Enumerable.Range(1, MachineCount).Select(id => new Machine { Id = id, Name = $"m{id}", IsDisabled = false, Endpoint = """{"communicationStyle":"KubernetesApi"}""" }).ToList();
            _unreachable = Enumerable.Range(1, unreachable).ToHashSet();

            var setting = new MachineHealthCheckSetting { SweepDeadlineSeconds = deadlineSeconds, MaxParallelism = Parallelism };

            if (timeoutSeconds != null) setting.ProbeTimeoutSeconds = timeoutSeconds.Value;

            _host = new HealthCheckTestHost(BuildMachines().Object, Mock.Of<IMachinePolicyDataProvider>(), BuildRegistry().Object, HealthCheckTestHost.PassthroughRedis().Object, setting, null, ProbeTimeouts, _clock);
            ExpectedTimeout = _host.Container.Resolve<IMachineHealthCheckCoordinator>().ProbeTimeoutFor(CommunicationStyle.KubernetesApi, null);
        }

        public ConcurrentDictionary<int, byte> Started { get; } = new();

        public ConcurrentQueue<string> Violations { get; } = new();

        public List<TimeSpan> ProbeTimeouts { get; } = new();

        public TimeSpan ExpectedTimeout { get; }

        public IEnumerable<int> NeverStarted => _machines.Select(m => m.Id).Where(id => !Started.ContainsKey(id));

        public IEnumerable<TimeSpan> ArmedProbeTimers => _clock.Armed.Where(t => t != _deadline && t != TimeSpan.FromSeconds(1)).Concat(ProbeTimeouts);

        public void Dispose() => _host.Dispose();

        public async Task RunSweepAsync()
        {
            _sweepDeadlineAt = _clock.GetUtcNow() + _deadline;

            var sweep = _host.Service.AutoHealthCheckForAllAsync();

            while (await NextHungProbeAsync(sweep).ConfigureAwait(false) is { } hung)
                await LetTimeRunUntilItEndsAsync(hung).ConfigureAwait(false);

            await sweep.WaitAsync(Guard).ConfigureAwait(false);
        }

        // The sweep either finishes, or (one worker) blocks on a probe that never answers.
        private async Task<HungProbe> NextHungProbeAsync(Task sweep)
        {
            await Task.WhenAny(sweep, _hung.Reader.WaitToReadAsync().AsTask()).WaitAsync(Guard).ConfigureAwait(false);

            return _hung.Reader.TryRead(out var ended) ? ended : null;
        }

        // A hung probe ends exactly when its own timeout or the sweep deadline cancels it, whichever comes first.
        private async Task LetTimeRunUntilItEndsAsync(HungProbe hung)
        {
            var endsAt = Min(hung.StartedAt + ExpectedTimeout, _sweepDeadlineAt);
            var now = _clock.GetUtcNow();

            if (endsAt > now) _clock.Advance(endsAt - now);

            await hung.Ended.Task.WaitAsync(Guard).ConfigureAwait(false);
        }

        private Mock<IMachineDataProvider> BuildMachines()
        {
            var machines = new Mock<IMachineDataProvider>();

            machines.Setup(p => p.GetMachinesAllSpacesLeastRecentlyCheckedFirstAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => ListLikeTheDatabase());
            machines.Setup(p => p.GetMachinesByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync((int id, CancellationToken _) => ReloadBeforeProbe(id));
            machines.Setup(p => p.UpdateMachineAsync(It.IsAny<Machine>(), It.IsAny<bool>(), It.IsAny<CancellationToken>())).Callback<Machine, bool, CancellationToken>((m, _, _) => CheckVerdict(m)).Returns(Task.CompletedTask);

            return machines;
        }

        // The due-list query takes `listing` of sweep time and returns the database's order: never checked first, then oldest check, then id.
        private List<Machine> ListLikeTheDatabase()
        {
            if (_listing > TimeSpan.Zero) _clock.Advance(_listing);

            return _machines.OrderBy(m => m.HealthLastChecked != null).ThenBy(m => m.HealthLastChecked).ThenBy(m => m.Id).ToList();
        }

        // The sweep reloads each machine just before probing it; that takes `setup` of sweep time.
        private Machine ReloadBeforeProbe(int id)
        {
            if (_setup > TimeSpan.Zero) _clock.Advance(_setup);

            return _machines.Single(m => m.Id == id);
        }

        private Mock<ITransportRegistry> BuildRegistry()
        {
            var checker = new Mock<IHealthCheckStrategy>();
            var transport = new Mock<IDeploymentTransport>();
            var registry = new Mock<ITransportRegistry>();

            checker.Setup(h => h.CheckHealthAsync(It.IsAny<Machine>(), It.IsAny<MachineConnectivityPolicyDto>(), It.IsAny<CancellationToken>(), It.IsAny<MachineHealthCheckPolicyDto>())).Returns<Machine, MachineConnectivityPolicyDto, CancellationToken, MachineHealthCheckPolicyDto>((m, _, ct, _) => ProbeAsync(m, ct));
            transport.Setup(t => t.HealthChecker).Returns(checker.Object);
            transport.Setup(t => t.CommunicationStyle).Returns(CommunicationStyle.KubernetesApi);
            registry.Setup(r => r.Resolve(It.IsAny<CommunicationStyle>())).Returns(transport.Object);

            return registry;
        }

        private async Task<HealthCheckResult> ProbeAsync(Machine machine, CancellationToken ct)
        {
            Started[machine.Id] = 0;
            _probeStart[machine.Id] = _clock.GetUtcNow();

            if (!_unreachable.Contains(machine.Id)) return new HealthCheckResult(true, "ok");

            var hung = new HungProbe(_probeStart[machine.Id], new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            try
            {
                _hung.Writer.TryWrite(hung);
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);

                return new HealthCheckResult(true, "never");
            }
            finally
            {
                hung.Ended.TrySetResult();
            }
        }

        // A verdict is recorded inside the sweep, and for a machine that never answers only after its FULL timeout ran out.
        private void CheckVerdict(Machine machine)
        {
            var now = _clock.GetUtcNow();

            if (now > _sweepDeadlineAt)
                Violations.Enqueue($"m{machine.Id} recorded {machine.HealthStatus} after the sweep deadline");

            if (_unreachable.Contains(machine.Id) && machine.HealthStatus != MachineHealthStatus.Unavailable)
                Violations.Enqueue($"unreachable m{machine.Id} recorded {machine.HealthStatus}");

            if (_unreachable.Contains(machine.Id) && now - _probeStart[machine.Id] < ExpectedTimeout)
                Violations.Enqueue($"unreachable m{machine.Id} recorded after {(now - _probeStart[machine.Id]).TotalSeconds}s, before its full {ExpectedTimeout.TotalSeconds}s timeout");

            if (!_unreachable.Contains(machine.Id) && machine.HealthStatus != MachineHealthStatus.Healthy)
                Violations.Enqueue($"reachable m{machine.Id} recorded {machine.HealthStatus}: {machine.HealthDetail}");
        }

        private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

        private sealed record HungProbe(DateTimeOffset StartedAt, TaskCompletionSource Ended);
    }
}
