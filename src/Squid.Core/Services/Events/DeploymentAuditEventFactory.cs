using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Enums.Events;
using Squid.Message.Models.Events;

namespace Squid.Core.Services.Events;

/// <summary>
/// Pure projection from the deployment pipeline's shared <see cref="DeploymentTaskContext"/>
/// to a <see cref="RecordEventRequest"/> for a given lifecycle <see cref="EventCategory"/>.
/// Kept side-effect-free so the mapping (which document FKs + references an audit event
/// carries) is unit-testable in isolation, with persistence handled separately by
/// <c>DeploymentAuditEventHandler</c>.
/// </summary>
public static class DeploymentAuditEventFactory
{
    /// <summary>
    /// Builds the audit request from the deployment context, or returns <c>null</c> when
    /// the context has no resolved deployment yet (nothing meaningful to attribute the
    /// event to — the caller skips recording).
    /// </summary>
    public static RecordEventRequest Build(DeploymentTaskContext ctx, EventCategory category)
    {
        var deployment = ctx?.Deployment;

        if (deployment == null) return null;

        return new RecordEventRequest
        {
            Category = category,
            SpaceId = deployment.SpaceId,
            ProjectId = deployment.ProjectId,
            ReleaseId = deployment.ReleaseId,
            EnvironmentId = deployment.EnvironmentId,
            DeploymentId = deployment.Id,
            ServerTaskId = ctx.ServerTaskId,
            References = BuildReferences(ctx)
        };
    }

    private static DeploymentEventReferences BuildReferences(DeploymentTaskContext ctx) => new()
    {
        Project = ctx.Project?.Name,
        Release = ctx.Release?.Version,
        Environment = ctx.Environment?.Name
    };
}
