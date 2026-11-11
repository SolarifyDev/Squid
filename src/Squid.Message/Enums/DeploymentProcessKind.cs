namespace Squid.Message.Enums;

/// <summary>
/// Identifies which aggregate owns a deployment process.
/// Values are persisted and must remain stable.
/// </summary>
public enum DeploymentProcessKind
{
    ProjectDeployment = 0,
    Runbook = 1
}
