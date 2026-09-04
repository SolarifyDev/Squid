namespace Squid.Core.Services.OctopusImport.Octopus;

public enum OctopusResourceKind
{
    Unknown,
    Project,
    ProjectGroup,
    Environment,
    Lifecycle,
    LifecyclePhase,
    Channel,
    DeploymentSettings,
    DeploymentProcess,
    DeploymentProcessSnapshot,
    DeploymentStep,
    DeploymentAction,
    VariableSet,
    VariableSetSnapshot,
    Variable,
    Feed,
    Team,
    Machine,
    Account,
    Certificate,
    Release,
    Deployment,
    ServerTask,
    ActionTemplate,
    WorkerPool
}
