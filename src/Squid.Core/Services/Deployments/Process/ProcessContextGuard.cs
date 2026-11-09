using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Message.Enums;

namespace Squid.Core.Services.Deployments.Process;

public enum ProcessContextKind
{
    ProjectDeployment = 1,
    Runbook = 2
}

public sealed record ProcessContext(
    int ProcessId,
    int ProjectId,
    int SpaceId,
    ProcessContextKind Kind,
    int? RunbookId);

public sealed record ProcessContextRequest(
    int ProcessId,
    int? SpaceId = null,
    int? ProjectId = null,
    int? RunbookId = null);

public interface IProcessContextGuard : IScopedDependency
{
    Task<ProcessContext> ResolveAsync(
        ProcessContextRequest request,
        bool allowRunbook,
        CancellationToken cancellationToken = default);

    Task<ProcessContext> ResolveForReadAsync(
        ProcessContextRequest request,
        CancellationToken cancellationToken = default);

    Task<ProcessContext> ResolveForWriteAsync(
        ProcessContextRequest request,
        CancellationToken cancellationToken = default);

    Task<ProcessContext> ResolveProjectDeploymentAsync(
        int processId,
        int? projectId = null,
        int? spaceId = null,
        int? runbookId = null,
        CancellationToken cancellationToken = default);

    Task EnsurePermissionAsync(
        ProcessContext context,
        bool write,
        CancellationToken cancellationToken = default);
}

public class ProcessContextGuard(
    IRepository repository,
    IAuthorizationService authorizationService,
    ICurrentUser currentUser) : IProcessContextGuard
{
    public Task<ProcessContext> ResolveAsync(
        ProcessContextRequest request,
        bool allowRunbook,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ResolveCoreAsync(request, allowRunbook, cancellationToken);
    }

    public async Task<ProcessContext> ResolveForReadAsync(
        ProcessContextRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(request, allowRunbook: true, cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(context, write: false, cancellationToken).ConfigureAwait(false);
        return context;
    }

    public async Task<ProcessContext> ResolveForWriteAsync(
        ProcessContextRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveAsync(request, allowRunbook: true, cancellationToken).ConfigureAwait(false);
        await EnsurePermissionAsync(context, write: true, cancellationToken).ConfigureAwait(false);
        return context;
    }

    public async Task<ProcessContext> ResolveProjectDeploymentAsync(
        int processId,
        int? projectId = null,
        int? spaceId = null,
        int? runbookId = null,
        CancellationToken cancellationToken = default)
    {
        var context = await ResolveCoreAsync(
            new ProcessContextRequest(processId, spaceId, projectId, runbookId),
            allowRunbook: false,
            cancellationToken).ConfigureAwait(false);

        if (context.Kind != ProcessContextKind.ProjectDeployment)
        {
            throw new ProcessContextMismatchException(
                $"Process {processId} is not a project deployment process.");
        }

        return context;
    }

    public async Task EnsurePermissionAsync(
        ProcessContext context,
        bool write,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (currentUser.IsInternal)
        {
            return;
        }

        var userId = currentUser.Id;
        if (userId == null)
        {
            var permission = ResolvePermission(context, write);
            throw new PermissionDeniedException(
                permission,
                "Authorization rejected: user identity not resolved.");
        }

        var requiredPermission = ResolvePermission(context, write);
        await authorizationService.EnsurePermissionAsync(
            new PermissionCheckRequest
            {
                UserId = userId.Value,
                Permission = requiredPermission,
                SpaceId = context.SpaceId,
                ProjectId = context.ProjectId
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProcessContext> ResolveCoreAsync(
        ProcessContextRequest request,
        bool allowRunbook,
        CancellationToken cancellationToken)
    {
        if (request.ProcessId <= 0)
        {
            throw new ProcessContextMismatchException("A positive ProcessId is required.");
        }

        if (request.ProjectId.HasValue && request.RunbookId.HasValue)
        {
            throw new ProcessContextMismatchException(
                "A process request cannot declare both ProjectId and RunbookId context.");
        }

        var process = await repository.QueryNoTracking<DeploymentProcess>(
                p => p.Id == request.ProcessId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (process == null)
        {
            throw new ProcessContextMismatchException($"DeploymentProcess {request.ProcessId} not found.");
        }

        if (request.SpaceId.HasValue && request.SpaceId.Value != process.SpaceId)
        {
            throw new ProcessContextMismatchException(
                $"Process {process.Id} does not belong to requested space {request.SpaceId.Value}.");
        }

        if (request.RunbookId.HasValue)
        {
            return process.ProcessKind switch
            {
                DeploymentProcessKind.Runbook => await ResolveRunbookContextAsync(process, request, cancellationToken).ConfigureAwait(false),
                DeploymentProcessKind.ProjectDeployment => throw new ProcessContextMismatchException(
                    $"Process {process.Id} is a project deployment process and cannot be used with Runbook {request.RunbookId.Value}."),
                _ => throw new ProcessContextMismatchException(
                    $"Process {process.Id} has unsupported process kind '{process.ProcessKind}'.")
            };
        }

        var project = await repository.QueryNoTracking<Persistence.Entities.Deployments.Project>(
                p => p.Id == process.ProjectId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (project == null || project.SpaceId != process.SpaceId)
        {
            throw new ProcessContextMismatchException(
                $"Process {process.Id} does not resolve to a project in its persisted space.");
        }

        return process.ProcessKind switch
        {
            DeploymentProcessKind.ProjectDeployment => ResolveProjectContext(process, request),
            DeploymentProcessKind.Runbook => allowRunbook
                ? await ResolveRunbookContextAsync(process, request, cancellationToken).ConfigureAwait(false)
                : throw new ProcessContextMismatchException(
                    $"Process {process.Id} is a Runbook process and cannot be used in the project deployment context."),
            _ => throw new ProcessContextMismatchException(
                $"Process {process.Id} has unsupported process kind '{process.ProcessKind}'.")
        };
    }

    private static ProcessContext ResolveProjectContext(
        DeploymentProcess process,
        ProcessContextRequest request)
    {
        if (request.RunbookId.HasValue)
        {
            throw new ProcessContextMismatchException(
                $"Process {process.Id} is a project deployment process and cannot be used with Runbook {request.RunbookId.Value}.");
        }

        if (process.OwnerId.HasValue)
        {
            throw new ProcessContextMismatchException(
                $"Project deployment process {process.Id} must not have an owner.");
        }

        if (request.ProjectId.HasValue && request.ProjectId.Value != process.ProjectId)
        {
            throw new ProcessContextMismatchException(
                $"Process {process.Id} does not belong to requested project {request.ProjectId.Value}.");
        }

        return new ProcessContext(
            process.Id,
            process.ProjectId,
            process.SpaceId,
            ProcessContextKind.ProjectDeployment,
            null);
    }

    private async Task<ProcessContext> ResolveRunbookContextAsync(
        DeploymentProcess process,
        ProcessContextRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.RunbookId.HasValue)
        {
            throw new ProcessContextMismatchException(
                $"Runbook process {process.Id} requires an explicit RunbookId context.");
        }

        if (!process.OwnerId.HasValue)
        {
            throw new ProcessContextMismatchException(
                $"Runbook process {process.Id} must have an owner.");
        }

        var runbook = await repository.QueryNoTracking<Runbook>(
                r => r.Id == request.RunbookId.Value)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (runbook == null)
        {
            throw new ProcessContextMismatchException($"Runbook {request.RunbookId.Value} not found.");
        }

        if (runbook.ProcessId != process.Id
            || process.OwnerId != runbook.Id
            || runbook.ProjectId != process.ProjectId
            || runbook.SpaceId != process.SpaceId)
        {
            throw new ProcessContextMismatchException(
                $"Runbook {runbook.Id} and process {process.Id} do not share the same persisted ownership.");
        }

        if (request.ProjectId.HasValue && request.ProjectId.Value != runbook.ProjectId)
        {
            throw new ProcessContextMismatchException(
                $"Runbook {runbook.Id} does not belong to requested project {request.ProjectId.Value}.");
        }

        return new ProcessContext(
            process.Id,
            runbook.ProjectId,
            runbook.SpaceId,
            ProcessContextKind.Runbook,
            runbook.Id);
    }

    private static Permission ResolvePermission(ProcessContext context, bool write)
    {
        return context.Kind switch
        {
            ProcessContextKind.Runbook when write => Permission.RunbookEdit,
            ProcessContextKind.Runbook => Permission.RunbookView,
            ProcessContextKind.ProjectDeployment when write => Permission.ProcessEdit,
            ProcessContextKind.ProjectDeployment => Permission.ProcessView,
            _ => throw new ProcessContextMismatchException(
                $"Process context kind '{context.Kind}' is not supported.")
        };
    }
}
