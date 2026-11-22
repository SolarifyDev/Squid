namespace Squid.Core.Services.Deployments.ServerTask.Lease;

/// <summary>
/// Identity of this server process on a task lease. The random suffix makes a restarted process on the same host (same machine name,
/// possibly a reused pid) a different owner, so a lease its predecessor left behind is never mistaken for its own.
/// </summary>
public static class ServerTaskLeaseOwner
{
    public static string Current { get; } = $"{HostName()}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    private static string HostName() => Environment.MachineName.Length <= 100 ? Environment.MachineName : Environment.MachineName[..100];
}
