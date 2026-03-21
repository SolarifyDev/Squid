namespace Squid.Message.Enums;

public enum HealthCheckScheduleType
{
    Interval = 0,
    Never = 1,
    Cron = 2,
}

public enum PolicyHealthCheckType
{
    RunScript = 0,
    OnlyConnectivity = 1,
}

public enum ScriptPolicyRunType
{
    InheritFromDefault = 0,
    CustomScript = 1,
}

public enum MachineConnectivityBehavior
{
    ExpectedToBeOnline = 0,
    MayBeOfflineAndCanBeSkipped = 1,
}

public enum DeleteMachinesBehavior
{
    DoNotDelete = 0,
    DeleteUnavailableMachines = 1,
}

public enum CalamariUpdateBehavior
{
    UpdateOnDeployment = 0,
    UpdateOnNewMachine = 1,
    UpdateAlways = 2,
}

public enum AgentUpdateBehavior
{
    NeverUpdate = 0,
    Update = 1,
}
