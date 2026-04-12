namespace Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

public enum ServiceMessageKind
{
    Unknown = 0,
    SetVariable = 1,
    CreateArtifact = 2,
    StepFailed = 3,
    StdWarning = 4
}
