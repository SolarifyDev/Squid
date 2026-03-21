using System.Text.Json.Serialization;

namespace Squid.Message.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthCheckScheduleType
{
    Interval = 0,
    Never = 1,
    Cron = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PolicyHealthCheckType
{
    RunScript = 0,
    OnlyConnectivity = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScriptPolicyRunType
{
    InheritFromDefault = 0,
    CustomScript = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MachineConnectivityBehavior
{
    ExpectedToBeOnline = 0,
    MayBeOfflineAndCanBeSkipped = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DeleteMachinesBehavior
{
    DoNotDelete = 0,
    DeleteUnavailableMachines = 1,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CalamariUpdateBehavior
{
    UpdateOnDeployment = 0,
    UpdateOnNewMachine = 1,
    UpdateAlways = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AgentUpdateBehavior
{
    NeverUpdate = 0,
    Update = 1,
}
