namespace Squid.Core.Services.Deployments.Rollback.Exceptions;

/// <summary>
/// Thrown when a rollback cannot be performed: no successful deployment
/// history for the environment, no prior release distinct from the current
/// one, or an operator-specified target release that never successfully
/// deployed to the environment. Inherits <see cref="InvalidOperationException"/>
/// so the global exception filter maps it to a client-actionable 4xx with the
/// message below.
/// </summary>
public class RollbackNotAvailableException : InvalidOperationException
{
    public int ProjectId { get; }

    public int EnvironmentId { get; }

    public RollbackNotAvailableException(int projectId, int environmentId, string reason)
        : base($"Cannot roll back project {projectId} in environment {environmentId}: {reason}")
    {
        ProjectId = projectId;
        EnvironmentId = environmentId;
    }
}
