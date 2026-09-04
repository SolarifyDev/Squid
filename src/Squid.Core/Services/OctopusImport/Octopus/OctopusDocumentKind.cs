namespace Squid.Core.Services.OctopusImport.Octopus;

public enum OctopusDocumentKind
{
    Unknown,
    Manifest,
    Project,
    ProjectGroup,
    Environment,
    Lifecycle,
    Channel,
    DeploymentSettings,
    DeploymentProcess,
    DeploymentProcessSnapshot,
    VariableSet,
    VariableSetSnapshot,
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
