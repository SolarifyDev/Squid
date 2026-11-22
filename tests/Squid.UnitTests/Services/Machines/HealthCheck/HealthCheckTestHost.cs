using Autofac;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines;
using Squid.Core.Services.Machines.HealthCheck;
using Squid.Core.Settings.Machines;
using Squid.Message.Enums.Caching;

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

    public HealthCheckTestHost(IMachineDataProvider machines, IMachinePolicyDataProvider policies, ITransportRegistry transports, IRedisSafeRunner redis, MachineHealthCheckSetting options = null, HealthCheckInProcessLocks locks = null)
    {
        options ??= new MachineHealthCheckSetting();
        locks ??= new HealthCheckInProcessLocks();

        var builder = new ContainerBuilder();
        builder.RegisterInstance(machines).As<IMachineDataProvider>();
        builder.RegisterInstance(policies).As<IMachinePolicyDataProvider>();
        builder.RegisterInstance(transports).As<ITransportRegistry>();
        builder.Register(c => new MachineHealthCheckCoordinator(redis, c.Resolve<ILifetimeScope>(), locks, options)).As<IMachineHealthCheckCoordinator>().InstancePerLifetimeScope();
        builder.RegisterType<MachineHealthCheckService>().AsSelf().InstancePerLifetimeScope();

        Container = builder.Build();
        Service = Container.Resolve<MachineHealthCheckService>();
        Coordinator = (MachineHealthCheckCoordinator)Container.Resolve<IMachineHealthCheckCoordinator>();
    }

    public void Dispose() => Container.Dispose();

    /// <summary>A Redis runner whose lock is always acquired; records every key it was asked for.</summary>
    public static Mock<IRedisSafeRunner> PassthroughRedis(List<string> lockKeys = null)
    {
        var redis = new Mock<IRedisSafeRunner>();

        redis.Setup(r => r.ExecuteWithLockAsync<object>(It.IsAny<string>(), It.IsAny<Func<Task<object>>>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<TimeSpan?>(), It.IsAny<RedisServer>()))
            .Returns<string, Func<Task<object>>, TimeSpan?, TimeSpan?, TimeSpan?, RedisServer>((key, logic, _, _, _, _) =>
            {
                lockKeys?.Add(key);
                return logic();
            });

        return redis;
    }
}
