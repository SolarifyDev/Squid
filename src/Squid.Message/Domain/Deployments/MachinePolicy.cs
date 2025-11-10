namespace Squid.Message.Domain.Deployments;

public class MachinePolicy : IEntity<string>
{
    public string Id { get; set; }

    public string SpaceId { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public bool IsDefault { get; set; }

    public string MachineHealthCheckPolicy { get; set; }

    public string MachineConnectivityPolicy { get; set; }

    public string MachineCleanupPolicy { get; set; }

    public string MachineUpdatePolicy { get; set; }

    public string MachineRpcCallRetryPolicy { get; set; }

    public string PollingRequestQueueTimeout { get; set; }

    public string ConnectionRetrySleepInterval { get; set; }

    public int ConnectionRetryCountLimit { get; set; }

    public string ConnectionRetryTimeLimit { get; set; }

    public string ConnectionConnectTimeout { get; set; }
}
