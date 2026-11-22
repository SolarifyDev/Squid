using Squid.Core.Services.Deployments.ServerTask.Lease;

namespace Squid.UnitTests.Support;

/// <summary>A lease keeper for tests that do not exercise leases: hands out a lease that is never lost and does nothing.</summary>
public sealed class NoLeaseKeeper : IServerTaskLeaseKeeper
{
    public static readonly NoLeaseKeeper Instance = new();

    public IServerTaskLease Start(int serverTaskId) => new NoLease();

    private sealed class NoLease : IServerTaskLease
    {
        public bool IsLost => false;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
