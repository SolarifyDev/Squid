namespace Squid.Core.Services.Deployments.Rollback;

/// <summary>
/// PR-12 — one successful deployment of a release to a single environment, as
/// read from the deployment journal (<c>DeploymentCompletion</c> joined to
/// <c>Deployment</c> + <c>Release</c>). The resolver returns these ordered
/// newest-first; <see cref="RollbackTargetSelector"/> consumes that ordering
/// to pick the rollback target.
/// </summary>
public sealed record RollbackReleaseHistoryEntry(int ReleaseId, string ReleaseVersion, int DeploymentId, DateTimeOffset CompletedTime);
