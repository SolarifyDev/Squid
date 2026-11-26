using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Services.Machines.Upgrade;
using Squid.IntegrationTests.Base;
using Squid.Message.Enums.Caching;
using Squid.Message.Models.Machines;
using StackExchange.Redis;

namespace Squid.IntegrationTests.Services.Machines.Upgrade;

/// <summary>
/// Real-container coverage for the upgrade-progress polling: the Hangfire job target resolves with its whole real
/// dependency graph (health check service, timeline store, Postgres), and the production upgrade service is handed the
/// timeline store it reads the pre-dispatch baseline from. A missing registration would only show in production, where a
/// poll job fails to activate or every upgrade loses its baseline.
/// </summary>
public sealed class UpgradeProgressPollingWiringIntegrationTests : TestBase
{
    private static readonly string RedisConnectionString = System.Environment.GetEnvironmentVariable("SQUID_TEST_REDIS_CONN_STRING") ?? "127.0.0.1:6379,password=,ssl=False,abortConnect=False,syncTimeout=5000";

    private static readonly Lazy<ConnectionMultiplexer> Redis = new(() => ConnectionMultiplexer.Connect(RedisConnectionString));

    public UpgradeProgressPollingWiringIntegrationTests() : base("UpgradeProgressPolling", "squid_it_upgrade_progress_polling")
    {
    }

    [Fact]
    public async Task ProgressPoller_ResolvesFromTheRealContainer_AndAPollForAnUnknownMachineDoesNotThrow()
    {
        var poll = new UpgradeProgressPoll { MachineId = 987654, FirstSignDeadline = DateTimeOffset.UtcNow.AddMinutes(2), Deadline = DateTimeOffset.UtcNow.AddMinutes(2) };

        await Run<IUpgradeProgressPoller>(async poller =>
        {
            poller.ShouldBeOfType<UpgradeProgressPoller>();

            await Should.NotThrowAsync(() => poller.PollAsync(poll, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60)));
        }, WithRedis);
    }

    [Fact]
    public async Task MachineUpgradeService_GetsTheSingletonTimelineStore_ForThePreDispatchBaseline()
    {
        await Run<IMachineUpgradeService, IUpgradeEventTimelineStore>((service, store) =>
        {
            var wired = typeof(MachineUpgradeService).GetField("_upgradeEventStore", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service);

            wired.ShouldBeSameAs(store, "without it the previous run's terminal status cannot be told apart from this run's");

            return Task.CompletedTask;
        }, WithRedis);
    }

    private static void WithRedis(ContainerBuilder builder)
    {
        builder.RegisterInstance(Redis.Value).Keyed<ConnectionMultiplexer>(RedisServer.System).ExternallyOwned();
        builder.RegisterInstance(Redis.Value).Keyed<ConnectionMultiplexer>(RedisServer.Vector).ExternallyOwned();
    }
}
