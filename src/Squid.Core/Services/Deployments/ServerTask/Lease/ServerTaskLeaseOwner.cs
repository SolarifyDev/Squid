namespace Squid.Core.Services.Deployments.ServerTask.Lease;

/// <summary>
/// Identity on a task lease. <see cref="Current"/> names this server process (the random suffix makes a restarted process on the same host, with a
/// possibly reused pid, a different one); <see cref="NewRun"/> names ONE pipeline run inside it. A lease belongs to a run, never to the process: two
/// runs of the same task in one process (a resume that starts while the previous run is still unwinding) must not share an owner, or the older run
/// would keep renewing, pausing and completing the newer run's row.
/// </summary>
public static class ServerTaskLeaseOwner
{
    public static string Current { get; } = $"{HostName()}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    /// <summary>A new owner identity for one pipeline run: the process identity plus a per-run suffix (at most 177 characters; the column holds 200).</summary>
    public static string NewRun() => $"{Current}/{Guid.NewGuid():N}";

    private static string HostName() => Environment.MachineName.Length <= 100 ? Environment.MachineName : Environment.MachineName[..100];
}
