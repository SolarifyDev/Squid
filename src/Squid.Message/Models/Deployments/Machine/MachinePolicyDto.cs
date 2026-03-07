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
}

public class MachineHealthCheckPolicyDto
{
    public int HealthCheckIntervalSeconds { get; set; } = 3600;

    // key = CommunicationStyle ("KubernetesAgent", "KubernetesApi", "Ssh", ...)
    public Dictionary<string, MachineScriptPolicyDto> ScriptPolicies { get; set; } = new();
}

public class MachineScriptPolicyDto
{
    public string RunType { get; set; } = "InheritFromDefault";

    public string ScriptBody { get; set; }
}

public class MachineConnectivityPolicyDto
{
    public string MachineConnectivityBehavior { get; set; } = "ExpectedToBeOnline";
}

public class MachineCleanupPolicyDto
{
    public string DeleteMachinesBehavior { get; set; } = "DoNotDelete";

    public int DeleteMachinesAfterSeconds { get; set; } = 86400;
}

public class MachineUpdatePolicyDto
{
    public string CalamariUpdateBehavior { get; set; } = "UpdateOnDeployment";

    public string TentacleUpdateBehavior { get; set; } = "NeverUpdate";
}
