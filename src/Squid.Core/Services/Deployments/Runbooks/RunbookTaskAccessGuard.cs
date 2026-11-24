using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Enums;
using Squid.Message.Models.Authorization;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.Core.Services.Deployments.Runbooks;

public interface IRunbookTaskAccessGuard : IScopedDependency
{
    Task EnsureCanViewAsync(ServerTaskEntity task, CancellationToken cancellationToken = default);
}

public sealed class RunbookTaskAccessGuard(
    IRunbookRunDataProvider runbookRunDataProvider,
    IRunbookDataProvider runbookDataProvider,
    IAuthorizationService authorizationService,
    ICurrentUser currentUser) : IRunbookTaskAccessGuard
{
    public async Task EnsureCanViewAsync(ServerTaskEntity task, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(task?.ServerTaskType, RunbookTaskTypes.Runbook, StringComparison.OrdinalIgnoreCase))
            return;

        var run = await runbookRunDataProvider
            .GetByServerTaskIdAsync(task.Id, cancellationToken)
            .ConfigureAwait(false);
        var runbook = run == null
            ? null
            : await runbookDataProvider
                .GetRunbookByIdAndProjectAsync(run.RunbookId, run.ProjectId, cancellationToken)
                .ConfigureAwait(false);

        if (run == null || runbook == null
            || runbook.ProjectId != run.ProjectId
            || runbook.SpaceId != run.SpaceId
            || task.ProjectId != run.ProjectId
            || task.SpaceId != run.SpaceId
            || task.EnvironmentId != run.EnvironmentId)
        {
            throw new DeploymentEntityNotFoundException("Task", task.Id);
        }

        if (currentUser.IsInternal)
            return;
        if (currentUser.Id == null)
            throw new PermissionDeniedException(Permission.RunbookView, "Authorization rejected: user identity not resolved.");

        await authorizationService.EnsurePermissionAsync(new PermissionCheckRequest
        {
            UserId = currentUser.Id.Value,
            Permission = Permission.RunbookView,
            ProjectId = run.ProjectId,
            SpaceId = run.SpaceId,
            EnvironmentId = run.EnvironmentId
        }, cancellationToken).ConfigureAwait(false);
    }
}
