namespace Squid.Message.Models.Deployments.Execution;

public enum ExecutionMode
{
    Unspecified = 0,
    DirectScript = 1,
    PackagedPayload = 2,
    ManualIntervention = 3
}

public enum ContextPreparationPolicy
{
    Unspecified = 0,
    Apply = 1,
    Skip = 2
}

public enum ExecutionLocation
{
    Unspecified = 0,
    ApiWorkerLocal = 1,
    RemoteTentacle = 2
}

public enum ExecutionBackend
{
    Unspecified = 0,
    LocalProcess = 1,
    HalibutScriptService = 2,
    HttpApi = 3
}

public enum PayloadKind
{
    Unspecified = 0,
    None = 1,
    YamlBundle = 2
}

public enum RunnerKind
{
    Unspecified = 0,
    Bash = 1,
    PowerShell = 2,
    SquidCalamariCli = 3
}
