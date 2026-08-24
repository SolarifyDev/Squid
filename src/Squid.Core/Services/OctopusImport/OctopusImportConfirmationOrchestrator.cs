using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Project;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Channel;
using Squid.Message.Models.Deployments.ProjectGroup;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportConfirmationOrchestrator : IScopedDependency
{
    Task<OctopusImportSessionDto> ConfirmAsync(
        OctopusImportConfirmationRequest request,
        CancellationToken ct = default);
}

public sealed record OctopusImportConfirmationRequest(
    Guid SessionId,
    int DestinationSpaceId,
    OctopusResourceGraph Graph,
    OctopusImportDependencyPlan DependencyPlan,
    OctopusImportPreviewPlanDto PreviewPlan);

public sealed class OctopusImportConfirmationOrchestrator : IOctopusImportConfirmationOrchestrator
{
    private readonly IOctopusImportSessionService _sessionService;
    private readonly IOctopusImportConflictDiscoveryService _conflictDiscoveryService;
    private readonly IOctopusImportPreviewValidator _previewValidator;
    private readonly IOctopusImportTransactionExecutor _transactionExecutor;
    private readonly IOctopusImportProjectMapper _projectMapper;
    private readonly IOctopusImportEnvironmentMapper _environmentMapper;
    private readonly IOctopusImportLifecycleMapper _lifecycleMapper;
    private readonly IOctopusImportFeedMapper _feedMapper;
    private readonly IOctopusImportVariableMapper _variableMapper;
    private readonly IOctopusImportDeploymentProcessMapper _processMapper;
    private readonly IOctopusImportExternalResourceShellMapper _externalResourceShellMapper;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IChannelDataProvider _channelDataProvider;
    private readonly IMediator _mediator;

    public OctopusImportConfirmationOrchestrator(
        IOctopusImportSessionService sessionService,
        IOctopusImportConflictDiscoveryService conflictDiscoveryService,
        IOctopusImportPreviewValidator previewValidator,
        IOctopusImportTransactionExecutor transactionExecutor,
        IOctopusImportProjectMapper projectMapper,
        IOctopusImportEnvironmentMapper environmentMapper,
        IOctopusImportLifecycleMapper lifecycleMapper,
        IOctopusImportFeedMapper feedMapper,
        IOctopusImportVariableMapper variableMapper,
        IOctopusImportDeploymentProcessMapper processMapper,
        IOctopusImportExternalResourceShellMapper externalResourceShellMapper,
        IProjectDataProvider projectDataProvider,
        IChannelDataProvider channelDataProvider,
        IMediator mediator)
    {
        _sessionService = sessionService;
        _conflictDiscoveryService = conflictDiscoveryService;
        _previewValidator = previewValidator;
        _transactionExecutor = transactionExecutor;
        _projectMapper = projectMapper;
        _environmentMapper = environmentMapper;
        _lifecycleMapper = lifecycleMapper;
        _feedMapper = feedMapper;
        _variableMapper = variableMapper;
        _processMapper = processMapper;
        _externalResourceShellMapper = externalResourceShellMapper;
        _projectDataProvider = projectDataProvider;
        _channelDataProvider = channelDataProvider;
        _mediator = mediator;
    }

    public async Task<OctopusImportSessionDto> ConfirmAsync(
        OctopusImportConfirmationRequest request,
        CancellationToken ct = default)
    {
        ValidateRequest(request);

        var current = await _sessionService
            .GetSessionAsync(request.SessionId, request.DestinationSpaceId, ct)
            .ConfigureAwait(false);

        if (OctopusImportSessionStateMachine.IsTerminal(current.State) || current.State == OctopusImportSessionState.Importing)
            return current;

        if (current.State != OctopusImportSessionState.Validated)
            throw new InvalidOperationException($"Octopus import confirmation requires a Validated session. Current state is '{current.State}'.");

        var admitted = await _sessionService
            .TryStartConfirmationAsync(request.SessionId, request.DestinationSpaceId, ct)
            .ConfigureAwait(false);

        if (!admitted)
            return await _sessionService.GetSessionAsync(request.SessionId, request.DestinationSpaceId, ct).ConfigureAwait(false);

        var result = BuildInitialResult(request.PreviewPlan);
        var validation = await RevalidateAsync(request, result, ct).ConfigureAwait(false);
        if (request.PreviewPlan.HasBlockers || validation.HasBlockers || result.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker))
            return await _sessionService
                .RecordResultAsync(request.SessionId, request.DestinationSpaceId, OctopusImportSessionState.Failed, result, ct)
                .ConfigureAwait(false);

        try
        {
            var context = new OctopusImportTransactionContext(request.SessionId, request.DestinationSpaceId);
            result = await _transactionExecutor
                .ExecuteInImportTransactionAsync(
                    context,
                    (_, transactionCt) => ExecuteAsync(request, result, transactionCt),
                    ct)
                .ConfigureAwait(false);

            result.Succeeded = true;
            return await _sessionService
                .RecordResultAsync(request.SessionId, request.DestinationSpaceId, OctopusImportSessionState.Succeeded, result, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            MarkRolledBack(result, ex);
            result.Succeeded = false;

            return await _sessionService
                .RecordResultAsync(request.SessionId, request.DestinationSpaceId, OctopusImportSessionState.Failed, result, ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<OctopusImportValidationResultDto> RevalidateAsync(
        OctopusImportConfirmationRequest request,
        OctopusImportSessionResultDto result,
        CancellationToken ct)
    {
        var conflicts = await _conflictDiscoveryService
            .DiscoverAsync(request.DestinationSpaceId, request.Graph, ct)
            .ConfigureAwait(false);
        var validation = _previewValidator.Validate(request.Graph, request.DependencyPlan, conflicts, request.PreviewPlan);

        foreach (var diagnostic in validation.Diagnostics)
            result.Diagnostics.Add(Redact(diagnostic));

        if (result.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker))
        {
            result.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportConfirmationDiagnosticCodes.ValidationBlockedConfirmation,
                "Octopus import confirmation was blocked because the validated plan no longer passes confirmation-time validation."));
        }

        return validation;
    }

    private async Task<OctopusImportSessionResultDto> ExecuteAsync(
        OctopusImportConfirmationRequest request,
        OctopusImportSessionResultDto result,
        CancellationToken ct)
    {
        var execution = new ConfirmationExecutionContext(request, result);

        try
        {
            foreach (var resource in request.DependencyPlan.OrderedResources)
            {
                if (execution.IsAlreadyCompleted(resource))
                    continue;

                await ExecuteResourceAsync(execution, resource, ct).ConfigureAwait(false);
            }

            foreach (var resource in request.DependencyPlan.OutOfScopeResources)
                execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Skipped);

            return result;
        }
        finally
        {
            execution.CopyMappingsToResult();
        }
    }

    private async Task ExecuteResourceAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var preview = execution.GetPreview(resource);
        if (preview == null)
        {
            execution.MarkFailed(resource, Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportConfirmationDiagnosticCodes.MissingPreviewResource,
                "The validated confirmation plan does not contain a matching preview resource.",
                resource));
            throw new OctopusImportConfirmationException("Confirmation preview resource is missing.");
        }

        switch (preview.PreviewAction)
        {
            case OctopusImportPreviewAction.ReuseExisting:
                ReuseResource(execution, resource, preview);
                return;
            case OctopusImportPreviewAction.Skip:
                execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Skipped);
                return;
            case OctopusImportPreviewAction.Unsupported:
                execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Unsupported);
                return;
            case OctopusImportPreviewAction.Blocked:
            case OctopusImportPreviewAction.RenameRequired:
                execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Blocked);
                return;
            case OctopusImportPreviewAction.Create:
                await CreateResourceAsync(execution, resource, ct).ConfigureAwait(false);
                return;
            default:
                execution.MarkFailed(resource, Diagnostic(
                    OctopusImportCompatibilitySeverity.Blocker,
                    OctopusImportConfirmationDiagnosticCodes.ResourceActionUnsupported,
                    $"Preview action '{preview.PreviewAction}' is not supported by confirmation.",
                    resource));
                throw new OctopusImportConfirmationException("Unsupported preview action.");
        }
    }

    private static void ReuseResource(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        OctopusImportResourceResultDto preview)
    {
        if (preview.DestinationId is not > 0)
        {
            execution.MarkFailed(resource, Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportConfirmationDiagnosticCodes.MissingReuseDestination,
                "Reusable preview resources must carry a destination id at confirmation time.",
                resource));
            throw new OctopusImportConfirmationException("Reusable resource has no destination id.");
        }

        execution.AddReused(resource, preview.DestinationId.Value);
    }

    private async Task CreateResourceAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        try
        {
            switch (resource.Kind)
            {
                case OctopusResourceKind.ProjectGroup:
                    await CreateProjectGroupAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Environment:
                    await CreateEnvironmentAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Lifecycle:
                    await CreateLifecycleAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Feed:
                    await CreateFeedAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Account:
                    await CreateAccountShellAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Project:
                    await CreateProjectAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Channel:
                    await CreateChannelAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.DeploymentSettings:
                    MarkDeploymentSettingsHandled(execution, resource);
                    return;
                case OctopusResourceKind.VariableSet:
                    await UpdateProjectVariableSetAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.DeploymentProcess:
                    await CreateDeploymentStepsAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.LifecyclePhase:
                case OctopusResourceKind.Variable:
                case OctopusResourceKind.DeploymentStep:
                case OctopusResourceKind.DeploymentAction:
                    execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Created);
                    return;
                default:
                    execution.MarkFailed(resource, Diagnostic(
                        OctopusImportCompatibilitySeverity.Blocker,
                        OctopusImportConfirmationDiagnosticCodes.ResourceTypeUnsupported,
                        $"Octopus {resource.Kind} resources are not created by the confirmation orchestrator.",
                        resource));
                    throw new OctopusImportConfirmationException("Unsupported resource type.");
            }
        }
        catch (Exception ex) when (ex is not OctopusImportConfirmationException)
        {
            execution.MarkFailed(resource, Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportConfirmationDiagnosticCodes.ResourceExecutionFailed,
                $"Octopus import confirmation failed while importing {resource.Kind} '{resource.Name}'.",
                resource));
            throw;
        }
    }

    private async Task CreateProjectGroupAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var group = resource.GetSource<OctopusProjectGroupDto>()
            ?? throw new OctopusImportConfirmationException("Project group source payload is missing.");
        var response = await _mediator
            .SendAsync<CreateProjectGroupCommand, CreateProjectGroupResponse>(
                new CreateProjectGroupCommand
                {
                    ProjectGroup = new CreateOrUpdateProjectGroupModel
                    {
                        Name = group.Name,
                        Description = group.Description,
                        Slug = group.Slug,
                        SpaceId = execution.DestinationSpaceId
                    }
                },
                ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, response.Data.Id);
    }

    private async Task CreateEnvironmentAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var mapping = _environmentMapper.MapToCreateModel(resource, execution.DestinationSpaceId);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        var response = await _mediator
            .SendAsync<CreateEnvironmentCommand, CreateEnvironmentResponse>(mapping.Environment, ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, response.Data.Environment.Id);
    }

    private async Task CreateLifecycleAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var mapping = _lifecycleMapper.MapToCreateOrUpdateModel(resource, execution.IdMap, execution.DestinationSpaceId);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        var response = await _mediator
            .SendAsync<CreateLifeCycleCommand, CreateLifeCycleResponse>(
                new CreateLifeCycleCommand { LifecyclePhase = mapping.Lifecycle },
                ct)
            .ConfigureAwait(false);

        var detail = response.Data.LifecyclePhase;
        execution.AddCreated(resource, detail.Lifecycle.Id);

        var phases = execution.CurrentResources
            .Where(r => r.Kind == OctopusResourceKind.LifecyclePhase
                        && string.Equals(r.ParentSourceId, resource.SourceId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.GetSource<OctopusLifecyclePhaseDto>()?.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (var i = 0; i < phases.Count && i < detail.Phases.Count; i++)
            execution.AddCreated(phases[i], detail.Phases[i].Id);
    }

    private async Task CreateFeedAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var mapping = _feedMapper.MapToCreateCommand(resource, execution.DestinationSpaceId);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        var response = await _mediator
            .SendAsync<CreateExternalFeedCommand, CreateExternalFeedResponse>(mapping.CreateCommand, ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, response.Data.ExternalFeed.Id);
    }

    private async Task CreateAccountShellAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var mapping = _externalResourceShellMapper.MapAccountToCreateCommand(resource, execution.IdMap, execution.DestinationSpaceId);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        var response = await _mediator
            .SendAsync<CreateDeploymentAccountCommand, CreateDeploymentAccountResponse>(mapping.CreateCommand, ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, response.Data.DeploymentAccount.Id);
    }

    private async Task CreateProjectAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var settings = GetOwnedChildResources(execution, resource.SourceId, OctopusResourceKind.DeploymentSettings)
            .Select(r => r.GetSource<OctopusDeploymentSettingsDto>())
            .FirstOrDefault(r => r != null);
        var defaultChannel = GetOwnedChildResources(execution, resource.SourceId, OctopusResourceKind.Channel)
            .Select(r => r.GetSource<OctopusChannelDto>())
            .FirstOrDefault(r => r != null);
        var mapping = _projectMapper.MapToCreateOrUpdateModel(resource, execution.IdMap, execution.DestinationSpaceId, settings, defaultChannel);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        var response = await _mediator
            .SendAsync<CreateProjectCommand, CreateProjectResponse>(
                new CreateProjectCommand { Project = mapping.Project },
                ct)
            .ConfigureAwait(false);

        var destination = response.Data;

        execution.AddCreated(resource, destination.Id);

    }

    private async Task CreateChannelAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var channel = resource.GetSource<OctopusChannelDto>()
            ?? throw new OctopusImportConfirmationException("Channel source payload is missing.");

        if (!execution.IdMap.TryGetDestinationId(channel.ProjectId, OctopusResourceKind.Project.ToString(), out var projectId))
            throw MappingBlocked(resource, $"Octopus channel project '{channel.ProjectId}' has not been mapped.");

        int? lifecycleId = null;
        if (!string.IsNullOrWhiteSpace(channel.LifecycleId)
            && execution.IdMap.TryGetDestinationId(channel.LifecycleId, OctopusResourceKind.Lifecycle.ToString(), out var mappedLifecycleId))
        {
            lifecycleId = mappedLifecycleId;
        }

        if (channel.Rules.Count > 0)
        {
            execution.AddDiagnostic(resource, Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportConfirmationDiagnosticCodes.ChannelRulesStoredAsMetadata,
                "Octopus channel version rules are not represented in the initial confirmation flow and must be reviewed manually.",
                resource));
        }

        var defaultChannel = await _channelDataProvider
            .GetDefaultChannelByProjectIdAsync(projectId, ct)
            .ConfigureAwait(false);
        if (defaultChannel == null)
            throw new OctopusImportConfirmationException("Default project channel was not created alongside the project.");

        defaultChannel.Name = channel.Name;
        defaultChannel.Description = null;
        defaultChannel.ProjectId = projectId;
        defaultChannel.LifecycleId = lifecycleId;
        defaultChannel.SpaceId = execution.DestinationSpaceId;
        defaultChannel.Slug = channel.Slug;
        defaultChannel.IsDefault = channel.IsDefault;

        await _channelDataProvider.UpdateChannelAsync(defaultChannel, forceSave: false, cancellationToken: ct).ConfigureAwait(false);

        execution.AddCreated(resource, defaultChannel.Id);
    }

    private static void MarkDeploymentSettingsHandled(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource)
    {
        execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Created);
        execution.AddDiagnostic(resource, Diagnostic(
            OctopusImportCompatibilitySeverity.Info,
            OctopusImportConfirmationDiagnosticCodes.DeploymentSettingsStoredAsProjectMetadata,
            "Octopus deployment settings were stored as non-sensitive project import metadata.",
            resource));
    }

    private async Task UpdateProjectVariableSetAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var source = resource.GetSource<OctopusVariableSetDto>()
            ?? throw new OctopusImportConfirmationException("Variable set source payload is missing.");
        if (!execution.IdMap.TryGetDestinationId(source.OwnerId, OctopusResourceKind.Project.ToString(), out var projectId))
            throw MappingBlocked(resource, "The destination project has not been mapped.");
        var project = await _projectDataProvider.GetProjectByIdAsync(projectId, ct).ConfigureAwait(false);
        var variableSetId = project?.VariableSetId ?? 0;
        if (variableSetId <= 0)
            throw new OctopusImportConfirmationException("Destination project variable set was not created.");

        var mapping = _variableMapper.MapToUpdateCommand(
            resource,
            execution.IdMap,
            variableSetId,
            execution.DestinationSpaceId,
            name: null,
            description: null);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        var response = await _mediator
            .SendAsync<UpdateVariableSetCommand, UpdateVariableSetResponse>(mapping.UpdateCommand, ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, variableSetId);

        var variableResources = source.Variables
            .Select((variable, index) => execution.FindResource(BuildChildSourceId(source.Id, "variable", variable.Id, index), OctopusResourceKind.Variable))
            .Where(r => r != null)
            .ToList();

        for (var i = 0; i < variableResources.Count && i < response.Data.VariableSet.Variables.Count; i++)
            execution.AddCreated(variableResources[i], response.Data.VariableSet.Variables[i].Id);
    }

    private async Task CreateDeploymentStepsAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var source = resource.GetSource<OctopusDeploymentProcessDto>()
            ?? throw new OctopusImportConfirmationException("Deployment process source payload is missing.");
        if (!execution.IdMap.TryGetDestinationId(source.OwnerId, OctopusResourceKind.Project.ToString(), out var projectId))
            throw MappingBlocked(resource, "The destination project has not been mapped.");
        var project = await _projectDataProvider.GetProjectByIdAsync(projectId, ct).ConfigureAwait(false);
        var processId = project?.DeploymentProcessId ?? 0;
        if (processId <= 0)
            throw new OctopusImportConfirmationException("Destination project deployment process was not created.");

        var mapping = _processMapper.MapToCreateStepCommands(resource, execution.IdMap, execution.DestinationSpaceId);
        execution.AddDiagnostics(resource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        execution.AddCreated(resource, processId);

        foreach (var stepMapping in mapping.Steps.OrderBy(s => s.SourceIndex))
        {
            var response = await _mediator
                .SendAsync<CreateDeploymentStepCommand, CreateDeploymentStepResponse>(stepMapping.CreateCommand, ct)
                .ConfigureAwait(false);

            var stepResource = execution.FindResource(stepMapping.SourceStepId, OctopusResourceKind.DeploymentStep);
            if (stepResource != null)
                execution.AddCreated(stepResource, response.Data.Id);

            var orderedActions = stepMapping.Actions.OrderBy(a => a.ActionIndex).ToList();
            for (var i = 0; i < orderedActions.Count && i < response.Data.Actions.Count; i++)
            {
                var actionMapping = orderedActions[i];
                var actionResource = execution.FindResource(actionMapping.SourceActionId, OctopusResourceKind.DeploymentAction);
                if (actionResource != null)
                    execution.AddCreated(actionResource, response.Data.Actions[i].Id);
            }
        }
    }

    private static IEnumerable<OctopusResourceNode> GetOwnedChildResources(
        ConfirmationExecutionContext execution,
        string ownerSourceId,
        OctopusResourceKind kind)
        => execution.CurrentResources.Where(r =>
            r.Kind == kind && string.Equals(r.ParentSourceId, ownerSourceId, StringComparison.OrdinalIgnoreCase));

    private static void EnsureNoBlockers(IEnumerable<OctopusImportDiagnosticDto> diagnostics)
    {
        if (diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker))
            throw new OctopusImportConfirmationException("Confirmation mapping produced blockers.");
    }

    private static OctopusImportConfirmationException MappingBlocked(OctopusResourceNode resource, string message)
    {
        return new OctopusImportConfirmationException(message, Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportConfirmationDiagnosticCodes.MappingBlockedConfirmation,
            message,
            resource));
    }

    private static OctopusImportSessionResultDto BuildInitialResult(OctopusImportPreviewPlanDto previewPlan)
    {
        return new OctopusImportSessionResultDto
        {
            Succeeded = false,
            Resources = previewPlan.Resources
                .Select(r => new OctopusImportResourceResultDto
                {
                    SourceId = r.SourceId,
                    SourceType = r.SourceType,
                    SourceName = r.SourceName,
                    PreviewAction = r.PreviewAction,
                    OutcomeState = r.OutcomeState == OctopusImportResourceOutcomeState.Pending
                        ? OctopusImportResourceOutcomeState.Pending
                        : r.OutcomeState,
                    DestinationId = r.DestinationId,
                    RequiredInputs = r.RequiredInputs.ToList(),
                    Diagnostics = r.Diagnostics.Select(Redact).ToList()
                })
                .ToList(),
            Diagnostics = previewPlan.Diagnostics.Select(Redact).ToList()
        };
    }

    private static void MarkRolledBack(OctopusImportSessionResultDto result, Exception exception)
    {
        result.Diagnostics.Add(Diagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack,
            $"Octopus import confirmation failed and the project transaction was rolled back ({exception.GetType().Name})."));

        result.IdMappings = result.IdMappings
            .Where(m => m.OutcomeState == OctopusImportResourceOutcomeState.Reused)
            .ToList();

        foreach (var resource in result.Resources.Where(r =>
                     r.OutcomeState is OctopusImportResourceOutcomeState.Created or OctopusImportResourceOutcomeState.Pending))
        {
            resource.OutcomeState = OctopusImportResourceOutcomeState.Failed;
            if (resource.DestinationId.HasValue)
                resource.DestinationId = null;

            resource.Diagnostics.Add(Diagnostic(
                OctopusImportCompatibilitySeverity.Blocker,
                OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack,
                "This resource was not completed because the import transaction was rolled back.",
                resource.SourceType,
                resource.SourceId,
                resource.SourceName));
        }
    }

    private static void ValidateRequest(OctopusImportConfirmationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.SessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(request));
        if (request.DestinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.DestinationSpaceId, "Destination space id must be positive.");
        ArgumentNullException.ThrowIfNull(request.Graph);
        ArgumentNullException.ThrowIfNull(request.DependencyPlan);
        ArgumentNullException.ThrowIfNull(request.PreviewPlan);
    }

    private static string BuildChildSourceId(string parentSourceId, string childKind, string sourceId, int index)
        => string.IsNullOrWhiteSpace(sourceId)
            ? $"{parentSourceId}/{childKind}-{index + 1}"
            : sourceId;

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        OctopusResourceNode resource = null)
        => Diagnostic(severity, code, message, resource?.Kind.ToString(), resource?.SourceId, resource?.Name);

    private static OctopusImportDiagnosticDto Diagnostic(
        OctopusImportCompatibilitySeverity severity,
        string code,
        string message,
        string resourceType,
        string sourceId,
        string resourceName)
        => Redact(new OctopusImportDiagnosticDto
        {
            Severity = severity,
            Code = code,
            Message = message,
            ResourceType = resourceType,
            SourceId = sourceId,
            ResourceName = resourceName
        });

    private static OctopusImportDiagnosticDto Redact(OctopusImportDiagnosticDto diagnostic)
        => OctopusImportRedaction.RedactDiagnostic(diagnostic);

    private sealed class ConfirmationExecutionContext
    {
        private readonly Dictionary<string, OctopusImportResourceResultDto> _resultsBySourceId;
        private readonly Dictionary<string, OctopusResourceNode> _resourcesByKey;
        private readonly OctopusImportSessionResultDto _result;

        public ConfirmationExecutionContext(
            OctopusImportConfirmationRequest request,
            OctopusImportSessionResultDto result)
        {
            Request = request;
            _result = result;
            IdMap = new OctopusImportIdMap();
            _resultsBySourceId = result.Resources
                .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            CurrentResources = request.DependencyPlan.OrderedResources
                .Where(r => !r.IsHistorical)
                .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
            _resourcesByKey = CurrentResources
                .GroupBy(r => Key(r.SourceId, r.Kind), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        public OctopusImportConfirmationRequest Request { get; }

        public int DestinationSpaceId => Request.DestinationSpaceId;

        public OctopusImportIdMap IdMap { get; }

        public IReadOnlyList<OctopusResourceNode> CurrentResources { get; }

        public OctopusImportResourceResultDto GetPreview(OctopusResourceNode resource)
            => _resultsBySourceId.TryGetValue(resource.SourceId, out var result) ? result : null;

        public bool IsAlreadyCompleted(OctopusResourceNode resource)
        {
            var result = GetPreview(resource);
            return result?.OutcomeState is OctopusImportResourceOutcomeState.Created
                or OctopusImportResourceOutcomeState.Skipped
                or OctopusImportResourceOutcomeState.Unsupported
                or OctopusImportResourceOutcomeState.Blocked
                or OctopusImportResourceOutcomeState.Failed;
        }

        public OctopusResourceNode FindResource(string sourceId, OctopusResourceKind kind)
            => _resourcesByKey.TryGetValue(Key(sourceId, kind), out var resource) ? resource : null;

        public void AddCreatedIfPresent(string sourceId, OctopusResourceKind kind, int destinationId)
        {
            var resource = FindResource(sourceId, kind);
            if (resource != null)
                AddCreated(resource, destinationId);
        }

        public void AddCreated(OctopusResourceNode resource, int destinationId)
        {
            if (!IdMap.TryGetDestinationId(resource, out _))
                IdMap.AddCreated(resource, destinationId);

            MarkOutcome(resource, OctopusImportResourceOutcomeState.Created, destinationId);
        }

        public void AddReused(OctopusResourceNode resource, int destinationId)
        {
            if (!IdMap.TryGetDestinationId(resource, out _))
                IdMap.AddReused(resource, destinationId);

            MarkOutcome(resource, OctopusImportResourceOutcomeState.Reused, destinationId);
        }

        public void MarkOutcome(
            OctopusResourceNode resource,
            OctopusImportResourceOutcomeState outcome,
            int? destinationId = null)
        {
            var result = EnsureResult(resource);
            result.OutcomeState = outcome;
            result.DestinationId = destinationId ?? result.DestinationId;
        }

        public void MarkFailed(OctopusResourceNode resource, OctopusImportDiagnosticDto diagnostic)
        {
            var result = EnsureResult(resource);
            result.OutcomeState = OctopusImportResourceOutcomeState.Failed;
            result.Diagnostics.Add(diagnostic);
            _result.Diagnostics.Add(diagnostic);
        }

        public void AddDiagnostic(OctopusResourceNode resource, OctopusImportDiagnosticDto diagnostic)
        {
            var redacted = Redact(diagnostic);
            EnsureResult(resource).Diagnostics.Add(redacted);
            _result.Diagnostics.Add(redacted);
        }

        public void AddDiagnostics(OctopusResourceNode resource, IEnumerable<OctopusImportDiagnosticDto> diagnostics)
        {
            foreach (var diagnostic in diagnostics ?? [])
                AddDiagnostic(resource, diagnostic);
        }

        public void CopyMappingsToResult()
        {
            IdMap.CopyTo(_result);
        }

        private OctopusImportResourceResultDto EnsureResult(OctopusResourceNode resource)
        {
            if (_resultsBySourceId.TryGetValue(resource.SourceId, out var result))
                return result;

            result = new OctopusImportResourceResultDto
            {
                SourceId = resource.SourceId,
                SourceType = resource.Kind.ToString(),
                SourceName = resource.Name,
                PreviewAction = OctopusImportPreviewAction.Create,
                OutcomeState = OctopusImportResourceOutcomeState.Pending
            };
            _resultsBySourceId.Add(resource.SourceId, result);
            _result.Resources.Add(result);
            return result;
        }

        private static string Key(string sourceId, OctopusResourceKind kind)
            => $"{kind}:{sourceId}".ToUpperInvariant();
    }

    private sealed class OctopusImportConfirmationException : Exception
    {
        public OctopusImportConfirmationException(string message)
            : base(message)
        {
        }

        public OctopusImportConfirmationException(string message, OctopusImportDiagnosticDto diagnostic)
            : base(message)
        {
            Diagnostic = diagnostic;
        }

        public OctopusImportDiagnosticDto Diagnostic { get; }
    }
}
