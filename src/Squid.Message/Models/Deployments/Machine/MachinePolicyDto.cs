using Squid.Message.Enums;

namespace Squid.Message.Models.Deployments.Machine;

public class MachinePolicyDto
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public bool IsDefault { get; set; }

    public MachineHealthCheckPolicyDto MachineHealthCheckPolicy { get; set; } = new();

    public MachineConnectivityPolicyDto MachineConnectivityPolicy { get; set; } = new();

    public MachineCleanupPolicyDto MachineCleanupPolicy { get; set; } = new();

    public MachineUpdatePolicyDto MachineUpdatePolicy { get; set; } = new();

    public MachineRpcCallRetryPolicyDto MachineRpcCallRetryPolicy { get; set; } = new();
}

public class MachineHealthCheckPolicyDto
{
    public HealthCheckScheduleType HealthCheckScheduleType { get; set; }

    public int HealthCheckIntervalSeconds { get; set; } = 3600;

    public string HealthCheckCronExpression { get; set; }

    public PolicyHealthCheckType HealthCheckType { get; set; }

    // key = ScriptSyntax.ToString() ("Bash", "PowerShell")
    public Dictionary<string, MachineScriptPolicyDto> ScriptPolicies { get; set; } = new();
}

public class MachineScriptPolicyDto
{
    public ScriptPolicyRunType RunType { get; set; }

    public string ScriptBody { get; set; }
}

public class MachineConnectivityPolicyDto
{
    public MachineConnectivityBehavior MachineConnectivityBehavior { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 60;

    public int RetryAttempts { get; set; } = 5;

    public int RetryWaitIntervalSeconds { get; set; } = 1;

    public int RetryTimeLimitSeconds { get; set; } = 300;

    public int PollingRequestQueueTimeoutSeconds { get; set; } = 600;
}

public class MachineCleanupPolicyDto
{
    public DeleteMachinesBehavior DeleteMachinesBehavior { get; set; }

    public int DeleteMachinesAfterSeconds { get; set; } = 86400;
}

public class MachineUpdatePolicyDto
{
    public CalamariUpdateBehavior CalamariUpdateBehavior { get; set; }

    public AgentUpdateBehavior TentacleUpdateBehavior { get; set; }

    public AgentUpdateBehavior KubernetesAgentUpdateBehavior { get; set; }

    public int? TentacleUpdateAccountId { get; set; }
}

public class MachineRpcCallRetryPolicyDto
{
    public bool Enabled { get; set; } = true;

    public int DeploymentRetryDurationSeconds { get; set; } = 150;

    public int HealthCheckRetryDurationSeconds { get; set; } = 150;
}
