using Halibut;
using Halibut.Diagnostics;
using Halibut.Queue;
using Halibut.Queue.MessageStreamWrapping;
using Halibut.Transport.Protocol;
using Squid.Core.Halibut.DataStreamStorage;
using Squid.Core.Halibut.RedisQueue.Encryption;
using Squid.Core.Services.Security;
using Squid.Core.Settings.Caching;
using Squid.Core.Settings.Halibut;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Contracts.Tentacle.V1;
using Squid.Message.Contracts.Tentacle.V2;

namespace Squid.Core.Halibut.RedisQueue;

/// <summary>Where the serializer Halibut builds for the runtime's queues is handed over: it exists only once the runtime is being built, but the Redis side that needs it is a container component, so the container disposes it.</summary>
public sealed class QueueSerializerSlot
{
    public QueueMessageSerializer Value { get; set; }
}

public static class HalibutQueueWiring
{
    /// <summary>
    /// Every contract an agent can be called through. Halibut learns a contract's message types only when a client or service for it is created on the runtime,
    /// and a pod that dequeues a request over Redis may never have created that client (the request was made on another pod), so it could not read the request.
    /// Every pod registers them all at startup.
    /// </summary>
    public static readonly Type[] ContractTypes = [typeof(IScriptService), typeof(ICapabilitiesService), typeof(IFileTransferService), typeof(IScriptServiceV1), typeof(ICapabilitiesServiceV1), typeof(IScriptServiceV2)];

    public static ITypeRegistry BuildContractRegistry()
    {
        var registry = new TypeRegistryBuilder().Build();

        registry.AddToMessageContract(ContractTypes);

        return registry;
    }

    public static void RegisterComponents(ContainerBuilder builder)
    {
        builder.Register(ctx => RedisQueueOptions.From(Optional<HalibutSetting>(ctx))).AsSelf().SingleInstance();
        builder.Register(ctx => new RedisQueueHealth(ctx.Resolve<RedisQueueOptions>())).As<IRedisQueueHealth>().SingleInstance();
        builder.Register(CreateKeyProtector).As<IQueueMessageKeyProtector>().SingleInstance();
        builder.Register(ctx => new QueueMessageKeys(ctx.Resolve<IQueueMessageKeyProtector>(), ctx.Resolve<RedisQueueOptions>(), TimeProvider.System)).As<IQueueMessageKeys>().SingleInstance();
        builder.Register(_ => new RedisFallbackLog(TimeProvider.System)).AsSelf().SingleInstance();
        builder.RegisterType<PostgresReplicaStore>().As<IReplicaStore>().SingleInstance();
        builder.RegisterType<QueueKeyCheck>().As<IQueueKeyCheck>().SingleInstance();
        builder.RegisterType<DataStreamOrphanTtlStartupCheck>().As<IStartable>().SingleInstance();
        builder.RegisterType<ReplicaPresence>().As<IReplicaPresence>().AsSelf().SingleInstance();
        builder.RegisterType<ReplicaHeartbeat>().As<IStartable>().AsSelf().SingleInstance();
        builder.RegisterType<QueueSealingStartupCheck>().As<IStartable>().SingleInstance();
        builder.RegisterType<QueueSerializerSlot>().AsSelf().SingleInstance();
        builder.Register(CreateRedisFactory).AsSelf().SingleInstance();
    }

    /// <summary>The queue of every polling agent: local first, Redis behind it, sealed in Redis. The runtime's wire protocol is untouched.</summary>
    public static HalibutRuntimeBuilder WithFailoverQueue(this HalibutRuntimeBuilder builder, IComponentContext ctx, HalibutTimeoutsAndLimits limits, ILogFactory logFactory)
    {
        var slot = ctx.Resolve<QueueSerializerSlot>();
        var redis = ctx.Resolve<LazyRedisPendingRequestQueueFactory>();
        var failover = new FailoverPendingRequestQueueFactory(limits, logFactory, redis, new FailoverQueueContext(ctx.Resolve<IRedisQueueHealth>(), ctx.Resolve<RedisQueueOptions>(), TimeProvider.System, ctx.Resolve<RedisFallbackLog>(), ctx.Resolve<IReplicaPresence>()));
        var wrappers = new MessageStreamWrappers([new QueueMessageStreamWrapper(ctx.Resolve<IQueueMessageKeys>())]);

        return builder.WithLogFactory(logFactory).WithTypeRegistry(BuildContractRegistry()).WithQueueMessageStreamWrappers(wrappers).WithPendingRequestQueueFactory(serializer =>
        {
            slot.Value = serializer;

            return failover;
        });
    }

    private static T Optional<T>(IComponentContext ctx) where T : class => ctx.TryResolve<T>(out var service) ? service : null;

    // The master-key service is only needed when a message is first sealed or opened, so the runtime still builds in a container that does not have it.
    private static QueueMessageKeyProtector CreateKeyProtector(IComponentContext ctx)
    {
        var scope = ctx.Resolve<ILifetimeScope>();

        return new QueueMessageKeyProtector(new Lazy<IVariableEncryptionService>(() => scope.Resolve<IVariableEncryptionService>()));
    }

    private static LazyRedisPendingRequestQueueFactory CreateRedisFactory(IComponentContext ctx)
    {
        var scope = ctx.Resolve<ILifetimeScope>();

        return new LazyRedisPendingRequestQueueFactory(() => CreateBackend(scope));
    }

    private static RedisQueueBackend CreateBackend(ILifetimeScope scope)
    {
        var connection = Optional<RedisCacheConnectionStringSetting>(scope)?.Value;
        var inputs = new RedisQueueBackendInputs(connection, scope.Resolve<RedisQueueOptions>(), scope.Resolve<IRedisQueueHealth>(), scope.Resolve<QueueSerializerSlot>().Value, scope.Resolve<DistributedDataStreamStore>(), HalibutModule.BuildTimeoutsAndLimits(), scope.Resolve<ILogFactory>());

        return new RedisQueueBackend(inputs);
    }
}
