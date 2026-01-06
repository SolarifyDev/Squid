namespace Squid.Message.Models.Deployments.Machine;

public class MachinePolicyDto
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public bool IsDefault { get; set; }

    public MachineHealthCheckPolicyDto MachineHealthCheckPolicy { get; set; }

    public MachineConnectivityPolicyDto MachineConnectivityPolicy { get; set; }

    public MachineCleanupPolicyDto MachineCleanupPolicy { get; set; }

    public MachineUpdatePolicyDto MachineUpdatePolicy { get; set; }

    public MachineRpcCallRetryPolicyDto MachineRpcCallRetryPolicy { get; set; }

    public string PollingRequestQueueTimeout { get; set; }

    public string ConnectionRetrySleepInterval { get; set; }

    public int ConnectionRetryCountLimit { get; set; }

    public string ConnectionRetryTimeLimit { get; set; }

    public string ConnectionConnectTimeout { get; set; }
}

public class MachineHealthCheckPolicyDto
{
    public PowerShellHealthCheckPolicyDto PowerShellHealthCheckPolicy { get; set; }

    public BashHealthCheckPolicyDto BashHealthCheckPolicy { get; set; }

    public string HealthCheckInterval { get; set; }

    public string HealthCheckCron { get; set; }

    public string HealthCheckCronTimezone { get; set; }

    public string HealthCheckType { get; set; }
}

public class PowerShellHealthCheckPolicyDto
{
    public string RunType { get; set; }

    public string ScriptBody { get; set; }
}

public class BashHealthCheckPolicyDto
{
    public string RunType { get; set; }

    public string ScriptBody { get; set; }
}

public class MachineConnectivityPolicyDto
{
    public string MachineConnectivityBehavior { get; set; }
}

public class MachineCleanupPolicyDto
{
    public string DeleteMachinesBehavior { get; set; }

    public string DeleteMachinesElapsedTimeSpan { get; set; }
}

public class MachineUpdatePolicyDto
{
    public string CalamariUpdateBehavior { get; set; }

    public string TentacleUpdateBehavior { get; set; }

    public string KubernetesAgentUpdateBehavior { get; set; }

    public string TentacleUpdateAccountId { get; set; }
}

public class MachineRpcCallRetryPolicyDto
{
    public bool Enabled { get; set; }

    public string RetryDuration { get; set; }

    public string HealthCheckRetryDuration { get; set; }
}
