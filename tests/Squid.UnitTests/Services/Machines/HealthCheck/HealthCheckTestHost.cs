using Autofac;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums;
using Squid.Message.Enums.Caching;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.UnitTests.Services.Machines.HealthCheck;

/// <summary>
/// Builds a real <see cref="MachineHealthCheckService"/> + real <see cref="MachineHealthCheckCoordinator"/> inside a real Autofac
/// container (the sweep resolves the service from a child scope per machine), with mocked data providers and Redis.
/// </summary>
internal sealed class HealthCheckTestHost : IDisposable
{
    public IContainer Container { get; }
    public MachineHealthCheckService Service { get; }
    public MachineHealthCheckCoordinator Coordinator { get; }

    public HealthCheckTestHost(IMachineDataProvider machines, IMachinePolicyDataProvider policies, ITransportRegistry transports, IRedisSafeRunner redis, MachineHealthCheckSetting options = null, HealthCheckInProcessLocks locks = null, List<TimeSpan> probeTimeouts = null)
    {
        options ??= new MachineHealthCheckSetting();
        locks ??= new HealthCheckInProcessLocks();

        var builder = new ContainerBuilder();
        builder.RegisterInstance(machines).As<IMachineDataProvider>();
        builder.RegisterInstance(policies).As<IMachinePolicyDataProvider>();
        builder.RegisterInstance(transports).As<ITransportRegistry>();
        builder.Register(c => WrapCoordinator(new MachineHealthCheckCoordinator(redis, c.Resolve<ILifetimeScope>(), locks, options), probeTimeouts)).As<IMachineHealthCheckCoordinator>().InstancePerLifetimeScope();
        builder.RegisterType<MachineHealthCheckService>().AsSelf().InstancePerLifetimeScope();

        Container = builder.Build();
        Service = Container.Resolve<MachineHealthCheckService>();
        Coordinator = Container.Resolve<IMachineHealthCheckCoordinator>() as MachineHealthCheckCoordinator;
    }

    public void Dispose() => Container.Dispose();

    private static IMachineHealthCheckCoordinator WrapCoordinator(MachineHealthCheckCoordinator real, List<TimeSpan> probeTimeouts) => probeTimeouts == null ? real : new TimeoutSpyCoordinator(real, probeTimeouts);

    /// <summary>Forwards to the real coordinator and records the timeout every probe was given.</summary>
    private sealed class TimeoutSpyCoordinator(IMachineHealthCheckCoordinator inner, List<TimeSpan> timeouts) : IMachineHealthCheckCoordinator
    {
        public TimeSpan ManualBusyWait => inner.ManualBusyWait;

        public Task<bool> TryRunSweepAsync(Func<CancellationToken, Task> sweep, CancellationToken ct) => inner.TryRunSweepAsync(sweep, ct);

        public Task<bool> TryRunForMachineAsync(int machineId, TimeSpan wait, Func<Task> work, CancellationToken ct) => inner.TryRunForMachineAsync(machineId, wait, work, ct);

        public Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct) => inner.RunProbeAsync(probe, ct);

        public Task<HealthCheckResult> RunProbeAsync(Func<CancellationToken, Task<HealthCheckResult>> probe, CancellationToken ct, TimeSpan timeout)
        {
            timeouts.Add(timeout);

            return inner.RunProbeAsync(probe, ct, timeout);
        }

        public TimeSpan ProbeTimeoutFor(CommunicationStyle style, MachineHealthCheckPolicyDto policy) => inner.ProbeTimeoutFor(style, policy);

        public Task ForEachInScopeAsync<T>(IReadOnlyList<T> items, Func<ILifetimeScope, T, CancellationToken, Task> work, CancellationToken ct) => inner.ForEachInScopeAsync(items, work, ct);
    }

    /// <summary>A Redis runner whose lock is always acquired; records every key it was asked for.</summary>
    public static Mock<IRedisSafeRunner> PassthroughRedis(List<string> lockKeys = null)
    {
        var redis = new Mock<IRedisSafeRunner>();

        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<object>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>((key, logic, _, _, _, _) =>
            {
                if (lockKeys != null) lock (lockKeys) lockKeys.Add(key);   // probes run in parallel: List<T> is not thread-safe
                return logic();
            });

        return redis;
    }
}
