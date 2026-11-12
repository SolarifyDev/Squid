using System.Text.Json;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Teams;
using Squid.Message.Commands.Deployments.Account;
using Squid.Message.Commands.Deployments.Channel;
using Squid.Message.Commands.Deployments.Environment;
using Squid.Message.Commands.Deployments.ExternalFeed;
using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.Process.Step;
using Squid.Message.Commands.Deployments.Project;
using Squid.Message.Commands.Deployments.ProjectGroup;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Commands.Deployments.Variable;
using Squid.Message.Commands.Teams;
using Squid.Message.Enums;
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
    private readonly IOctopusImportDeploymentHistoryImporter _deploymentHistoryImporter;
    private readonly IOctopusImportLibraryVariableSetNameLock _libraryNameLock;
    private readonly IProjectDataProvider _projectDataProvider;
    private readonly IChannelDataProvider _channelDataProvider;
    private readonly IMediator _mediator;
    private readonly ITeamService _teamService;

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
        IOctopusImportDeploymentHistoryImporter deploymentHistoryImporter,
        IOctopusImportLibraryVariableSetNameLock libraryNameLock,
        IProjectDataProvider projectDataProvider,
        IChannelDataProvider channelDataProvider,
        IMediator mediator,
        ITeamService teamService)
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
        _deploymentHistoryImporter = deploymentHistoryImporter;
        _libraryNameLock = libraryNameLock;
        _projectDataProvider = projectDataProvider;
        _channelDataProvider = channelDataProvider;
        _mediator = mediator;
        _teamService = teamService;
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

        OctopusImportSessionResultDto result = null;
        try
        {
            result = BuildInitialResult(request.PreviewPlan);
            var validation = await RevalidateAsync(request, result, ct).ConfigureAwait(false);
            if (request.PreviewPlan.HasBlockers || validation.HasBlockers || result.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker))
                return await _sessionService
                    .RecordResultAsync(request.SessionId, request.DestinationSpaceId, OctopusImportSessionState.Failed, result, CancellationToken.None)
                    .ConfigureAwait(false);

            var context = new OctopusImportTransactionContext(request.SessionId, request.DestinationSpaceId);
            return await _transactionExecutor
                .ExecuteInImportTransactionAsync(
                    context,
                    async (_, transactionCt) =>
                    {
                        result = await ExecuteAsync(request, result, transactionCt).ConfigureAwait(false);
                        result.Succeeded = true;

                        return await _sessionService
                            .RecordResultAsync(
                                request.SessionId,
                                request.DestinationSpaceId,
                                OctopusImportSessionState.Succeeded,
                                result,
                                transactionCt)
                            .ConfigureAwait(false);
                    },
                    ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result ??= new OctopusImportSessionResultDto();
            MarkRolledBack(result, ex);
            result.Succeeded = false;

            return await _sessionService
                .RecordResultAsync(request.SessionId, request.DestinationSpaceId, OctopusImportSessionState.Failed, result, CancellationToken.None)
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
            await LockCreatedLibraryNamesAsync(execution, ct).ConfigureAwait(false);

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
            case OctopusImportPreviewAction.AlreadyImported:
                ReuseImportedResource(execution, resource, preview);
                return;
            case OctopusImportPreviewAction.Update:
                RejectUpdate(execution, resource);
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

    // An earlier confirmation already wrote this resource. It maps to its prior destination; a child that
    // only inherited "already imported" from its owner has no row of its own, so it is recorded without one.
    private static void ReuseImportedResource(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportResourceResultDto preview)
    {
        EnsureBackedBySourceMap(execution, resource, preview);

        if (preview.DestinationId is > 0)
            execution.AddReused(resource, preview.DestinationId.Value);
        else
            execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Reused);

        execution.MarkImportedBefore(resource);
    }

    // The preview plan is posted by the client, so "already imported" and its destination id are trusted only
    // when the server-side source map backs them; anything else would map Octopus ids onto arbitrary rows.
    private static void EnsureBackedBySourceMap(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusImportResourceResultDto preview)
    {
        if (execution.IsBackedBySourceMap(resource, preview.DestinationId)) return;

        execution.MarkFailed(resource, Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportPreviewDiagnosticCodes.StalePreviewPlan, "This resource is marked as already imported, but no earlier import of it into this destination is recorded. Re-run the preview.", resource));

        throw new OctopusImportConfirmationException("Already-imported resource is not backed by the source map.");
    }

    // In-place updates of imported history are not wired yet: every kind is rejected explicitly.
    private static void RejectUpdate(ConfirmationExecutionContext execution, OctopusResourceNode resource)
    {
        execution.MarkFailed(resource, Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportConfirmationDiagnosticCodes.ResourceActionUnsupported, $"Updating an imported Octopus {resource.Kind} is not supported by confirmation.", resource));

        throw new OctopusImportConfirmationException("Unsupported update of an imported resource.");
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
                case OctopusResourceKind.Team:
                    await CreateTeamAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.LibraryVariableSet:
                    await CreateLibraryVariableSetAsync(execution, resource, ct).ConfigureAwait(false);
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
                    await ImportVariableSetAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.DeploymentProcess:
                    await CreateDeploymentStepsAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.Variable:
                    MarkVariableHandled(execution, resource);
                    return;
                case OctopusResourceKind.LifecyclePhase:
                case OctopusResourceKind.DeploymentStep:
                case OctopusResourceKind.DeploymentAction:
                    execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Created);
                    return;
                case OctopusResourceKind.Release:
                    await CreateReleaseAsync(execution, resource, ct).ConfigureAwait(false);
                    return;
                case OctopusResourceKind.ServerTask:
                case OctopusResourceKind.Deployment:
                    await ImportDeploymentHistoryAsync(execution, resource, ct).ConfigureAwait(false);
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
            Log.Error(
                ex,
                "Octopus import confirmation failed while importing {ResourceKind} {ResourceName} ({ResourceId}).",
                resource.Kind,
                resource.Name,
                resource.SourceId);
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
                        Description = group.Description ?? string.Empty,
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

    private async Task CreateTeamAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        var team = resource.GetSource<OctopusTeamDto>()
            ?? throw new OctopusImportConfirmationException("Team source payload is missing.");

        // Created through the team service, NOT a nested CreateTeamCommand: that public command is not
        // ISpaceScoped, so the authorization middleware would check TeamCreate at system scope and reject a
        // space-scoped importer mid-transaction. ConfirmOctopusImportCommandHandler has already enforced
        // TeamCreate for the destination space before any row was written, so no authorization is widened.
        var command = new CreateTeamCommand { Name = team.Name, Description = team.Description ?? string.Empty, SpaceId = execution.DestinationSpaceId };
        var created = await _teamService.CreateAsync(command, ct).ConfigureAwait(false);

        if (created?.Id is not > 0)
            throw new OctopusImportConfirmationException("Team creation did not return a destination team id.");

        execution.AddCreated(resource, created.Id);

        // Octopus users are not imported, so the team starts empty. That is deliberately fail-closed:
        // an approval step restricted to this team stays restricted to the team (plus the explicit
        // administrator override) instead of silently widening to "anyone".
        execution.AddDiagnostic(resource, Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportConfirmationDiagnosticCodes.TeamCreatedWithoutMembers,
            $"Team '{team.Name}' was created without members because Octopus users are not imported ({team.MemberUserIds?.Count ?? 0} source member(s), {team.ExternalSecurityGroups?.Count ?? 0} external group(s)). Add members so they can action manual interventions assigned to this team; until then a system administrator, or a user holding the {nameof(Permission.InterruptionSubmitOverride)} permission in this space (granted to Space Owner), can approve on the team's behalf.",
            resource));
    }

    private async Task CreateLibraryVariableSetAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        var library = resource.GetSource<OctopusLibraryVariableSetDto>()
            ?? throw new OctopusImportConfirmationException("Library variable set source payload is missing.");

        var variableSetResource = execution.FindResource(library.VariableSetId, OctopusResourceKind.VariableSet);
        var command = BuildLibraryVariableSetCommand(execution, resource, library, variableSetResource);

        await EnsureLibraryNameStillFreeAsync(execution, resource, library, ct).ConfigureAwait(false);

        var response = await _mediator.SendAsync<CreateVariableSetCommand, CreateVariableSetResponse>(command, ct).ConfigureAwait(false);
        var created = response?.Data?.VariableSet;

        if (created is not { Id: > 0, OwnerId: > 0 })
            throw new OctopusImportConfirmationException("Create library variable set command did not return destination ids.");

        execution.AddCreated(resource, created.OwnerId);

        if (variableSetResource != null)
            execution.AddCreated(variableSetResource, created.Id);
    }

    // Every library name this confirmation will create is locked ONCE, up front and in the lock's canonical
    // order, before anything is written. Locking per library in dependency order would let two confirmations
    // hold shared names in opposite order and deadlock; the per-library re-check below then only re-enters.
    private async Task LockCreatedLibraryNamesAsync(ConfirmationExecutionContext execution, CancellationToken ct)
    {
        var names = execution.Request.DependencyPlan.OrderedResources
            .Where(r => r.Kind == OctopusResourceKind.LibraryVariableSet && !execution.IsAlreadyCompleted(r) && execution.GetPreview(r)?.PreviewAction == OctopusImportPreviewAction.Create)
            .Select(r => r.GetSource<OctopusLibraryVariableSetDto>()?.Name)
            .Where(n => n != null)
            .ToList();

        if (names.Count == 0) return;

        await _libraryNameLock.LockNamesAsync(execution.DestinationSpaceId, names, ct).ConfigureAwait(false);
    }

    // Preview planned a CREATE, but another confirmation (or an earlier library in this one) may have created
    // the same name since. The name lock serialises concurrent creators until commit, and the re-check turns
    // the loser into a clean, explained failure instead of a duplicate shared library.
    private async Task EnsureLibraryNameStillFreeAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusLibraryVariableSetDto library, CancellationToken ct)
    {
        var existingId = await _libraryNameLock.LockNameAndFindExistingAsync(execution.DestinationSpaceId, library.Name, ct).ConfigureAwait(false);

        if (existingId == null) return;

        var message = $"Library variable set '{library.Name}' was created in the destination space (id {existingId}) after this import was previewed. Nothing was imported; preview the export again to reuse the existing library.";

        execution.MarkFailed(resource, Diagnostic(OctopusImportCompatibilitySeverity.Blocker, OctopusImportConfirmationDiagnosticCodes.LibraryVariableSetCreatedConcurrently, message, resource));

        throw new OctopusImportConfirmationException(message);
    }

    private CreateVariableSetCommand BuildLibraryVariableSetCommand(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusLibraryVariableSetDto library, OctopusResourceNode variableSetResource)
    {
        if (variableSetResource == null)
        {
            execution.AddDiagnostic(resource, Diagnostic(
                OctopusImportCompatibilitySeverity.Warning,
                OctopusImportConfirmationDiagnosticCodes.LibraryVariableSetVariablesMissing,
                $"Library variable set '{library.Name}' was exported without its variables document; it was created empty.",
                resource));

            return new CreateVariableSetCommand { Name = library.Name, Description = library.Description, OwnerType = VariableSetOwnerType.LibraryVariableSet, SpaceId = execution.DestinationSpaceId, Variables = [] };
        }

        var mapping = _variableMapper.MapToCreateCommand(variableSetResource, execution.IdMap, execution.DestinationSpaceId, library.Name, library.Description);

        execution.AddDiagnostics(variableSetResource, mapping.Diagnostics);
        EnsureNoBlockers(mapping.Diagnostics);

        return mapping.CreateCommand;
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
            .FirstOrDefault(r => r?.IsDefault == true);
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

        var source = resource.GetSource<OctopusProjectDto>()
            ?? throw new OctopusImportConfirmationException("Project source payload is missing.");
        execution.AddCreatedMappingIfPresent(source.VariableSetId, OctopusResourceKind.VariableSet, destination.VariableSetId);
        execution.AddCreatedMappingIfPresent(source.DeploymentProcessId, OctopusResourceKind.DeploymentProcess, destination.DeploymentProcessId);
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

        if (channel.IsDefault)
        {
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
            defaultChannel.IsDefault = true;

            await _channelDataProvider.UpdateChannelAsync(defaultChannel, forceSave: false, cancellationToken: ct).ConfigureAwait(false);
            execution.AddCreated(resource, defaultChannel.Id);
            return;
        }

        var response = await _mediator
            .SendAsync<CreateChannelCommand, CreateChannelResponse>(
                new CreateChannelCommand
                {
                    Channel = new CreateOrUpdateChannelModel
                    {
                        Name = channel.Name,
                        Description = null,
                        ProjectId = projectId,
                        LifecycleId = lifecycleId,
                        SpaceId = execution.DestinationSpaceId,
                        Slug = channel.Slug,
                        IsDefault = false
                    }
                },
                ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, response.Data.Id);
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

    private async Task ImportVariableSetAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, CancellationToken ct)
    {
        var source = resource.GetSource<OctopusVariableSetDto>()
            ?? throw new OctopusImportConfirmationException("Variable set source payload is missing.");

        if (!execution.IsWrittenByImport(resource))
        {
            KeepReusedLibraryVariables(execution, resource, source);
            return;
        }

        await UpdateProjectVariableSetAsync(execution, resource, source, ct).ConfigureAwait(false);
    }

    // A library variable set's variables are written together with the set in
    // CreateLibraryVariableSetAsync, so reaching here means the set itself was REUSED (or not
    // created). Never rewrite an existing shared library's variables from an import.
    private static void KeepReusedLibraryVariables(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusVariableSetDto source)
    {
        if (!execution.IdMap.TryGetDestinationId(source.OwnerId, OctopusResourceKind.LibraryVariableSet.ToString(), out _))
            throw MappingBlocked(resource, $"Octopus library variable set '{source.OwnerId}' has not been mapped.");

        execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Skipped);
        execution.AddDiagnostic(resource, Diagnostic(
            OctopusImportCompatibilitySeverity.Warning,
            OctopusImportConfirmationDiagnosticCodes.ReusedLibraryVariableSetVariablesKept,
            "The destination already has a library variable set with this name; it was linked as-is and its variables were not modified. Review it against the Octopus source values.",
            resource));
    }

    private static void MarkVariableHandled(ConfirmationExecutionContext execution, OctopusResourceNode resource)
        => execution.MarkOutcome(resource, execution.IsWrittenByImport(resource) ? OctopusImportResourceOutcomeState.Created : OctopusImportResourceOutcomeState.Skipped);

    private async Task UpdateProjectVariableSetAsync(ConfirmationExecutionContext execution, OctopusResourceNode resource, OctopusVariableSetDto source, CancellationToken ct)
    {
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
            name: project.Name,
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

        var mapping = _processMapper.MapToCreateStepCommands(resource, execution.IdMap, execution.DestinationSpaceId, CollectActionTemplates(execution.Request.Graph));
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

    // Step templates are not imported as Squid resources (Squid has no template library), but their
    // parameter definitions/defaults are needed to materialise template-based steps faithfully.
    private static IReadOnlyDictionary<string, OctopusActionTemplateDto> CollectActionTemplates(OctopusResourceGraph graph)
        => graph.Resources
            .Where(r => r.Kind == OctopusResourceKind.ActionTemplate && !r.IsHistorical)
            .Select(ToActionTemplate)
            .Where(t => !string.IsNullOrWhiteSpace(t?.Id))
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    private static OctopusActionTemplateDto ToActionTemplate(OctopusResourceNode resource) => resource.GetSource<OctopusActionTemplateDto>();

    private async Task CreateReleaseAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        var source = resource.GetSource<OctopusReleaseDto>()
            ?? throw new OctopusImportConfirmationException("Release source payload is missing.");

        if (!execution.IdMap.TryGetDestinationId(source.ProjectId, OctopusResourceKind.Project.ToString(), out var projectId))
            throw MappingBlocked(resource, $"Octopus release project '{source.ProjectId}' has not been mapped.");

        if (!execution.IdMap.TryGetDestinationId(source.ChannelId, OctopusResourceKind.Channel.ToString(), out var channelId))
            throw MappingBlocked(resource, $"Octopus release channel '{source.ChannelId}' has not been mapped.");

        var selectedPackages = BuildReleaseSelectedPackages(execution, resource, source);
        var response = await _mediator
            .SendAsync<CreateReleaseCommand, CreateReleaseResponse>(
                new CreateReleaseCommand
                {
                    SpaceId = execution.DestinationSpaceId,
                    ProjectId = projectId,
                    ChannelId = channelId,
                    Version = string.IsNullOrWhiteSpace(source.Version) ? resource.Name : source.Version,
                    ReleaseNote = source.ReleaseNotes,
                    SelectedPackages = selectedPackages,
                    HistoricalCreatedDate = source.Assembled
                },
                ct)
            .ConfigureAwait(false);

        if (response?.Data?.Id is not > 0)
            throw new OctopusImportConfirmationException("Create release command did not return a destination release id.");

        execution.AddCreated(resource, response.Data.Id);
    }

    private async Task ImportDeploymentHistoryAsync(
        ConfirmationExecutionContext execution,
        OctopusResourceNode resource,
        CancellationToken ct)
    {
        if (resource.Kind == OctopusResourceKind.ServerTask)
        {
            execution.MarkOutcome(resource, OctopusImportResourceOutcomeState.Created);
            return;
        }

        var deployment = resource.GetSource<OctopusDeploymentDto>()
            ?? throw new OctopusImportConfirmationException("Deployment source payload is missing.");
        var taskResource = execution.FindResource(deployment.TaskId, OctopusResourceKind.ServerTask);
        var task = taskResource?.GetSource<OctopusServerTaskDto>()
            ?? throw new OctopusImportConfirmationException("Octopus deployment server task source payload is missing.");

        if (!execution.IdMap.TryGetDestinationId(deployment.ProjectId, OctopusResourceKind.Project.ToString(), out var projectId))
            throw MappingBlocked(resource, $"Octopus deployment project '{deployment.ProjectId}' has not been mapped.");
        if (!execution.IdMap.TryGetDestinationId(deployment.ChannelId, OctopusResourceKind.Channel.ToString(), out var channelId))
            throw MappingBlocked(resource, $"Octopus deployment channel '{deployment.ChannelId}' has not been mapped.");
        if (!execution.IdMap.TryGetDestinationId(deployment.ReleaseId, OctopusResourceKind.Release.ToString(), out var releaseId))
            throw MappingBlocked(resource, $"Octopus deployment release '{deployment.ReleaseId}' has not been mapped.");
        if (!execution.IdMap.TryGetDestinationId(deployment.EnvironmentId, OctopusResourceKind.Environment.ToString(), out var environmentId))
            throw MappingBlocked(resource, $"Octopus deployment environment '{deployment.EnvironmentId}' has not been mapped.");

        var created = deployment.Created ?? task.QueueTime ?? task.StartTime ?? DateTimeOffset.UtcNow;
        var queueTime = task.QueueTime ?? created;
        var result = await _deploymentHistoryImporter.ImportAsync(
            new OctopusImportDeploymentHistoryRequest(
                execution.DestinationSpaceId,
                projectId,
                channelId,
                releaseId,
                environmentId,
                deployment.Name ?? resource.Name,
                JsonSerializer.Serialize(deployment),
                deployment.DeployedBy,
                deployment.DeployedById,
                JsonSerializer.Serialize(deployment.DeployedToMachineIds ?? []),
                created,
                task.Name,
                task.Description ?? string.Empty,
                queueTime,
                task.StartTime,
                task.CompletedTime,
                task.State,
                task.ErrorMessage ?? string.Empty,
                task.HasWarningsOrErrors,
                task.DurationSeconds),
            ct)
            .ConfigureAwait(false);

        execution.AddCreated(resource, result.DeploymentId);
        if (taskResource != null)
            execution.AddCreated(taskResource, result.TaskId);
    }

    private static List<CreateReleaseSelectedPackageDto> BuildReleaseSelectedPackages(
        ConfirmationExecutionContext execution,
        OctopusResourceNode releaseResource,
        OctopusReleaseDto release)
    {
        var selectedPackages = release.SelectedPackages ?? [];
        if (selectedPackages.Count == 0) return [];

        return selectedPackages
            .Where(package => !string.IsNullOrWhiteSpace(package.ActionName)
                              && !string.IsNullOrWhiteSpace(package.Version))
            .Select(package =>
            {
                if (!TryResolveReleaseSelectedPackageFeedId(execution, release, package, out var feedId))
                {
                    execution.MarkFailed(releaseResource, Diagnostic(
                        OctopusImportCompatibilitySeverity.Blocker,
                        OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping,
                        $"Octopus release package selection for action '{package.ActionName}' references a package feed that has not been mapped to a destination Squid feed.",
                        releaseResource));
                    throw new OctopusImportConfirmationException("Release selected package feed mapping is missing.");
                }

                return new CreateReleaseSelectedPackageDto
                {
                    ActionName = package.ActionName,
                    PackageReferenceName = package.PackageReferenceName ?? string.Empty,
                    Version = package.Version,
                    FeedId = feedId
                };
            })
            .ToList();
    }

    private static bool TryResolveReleaseSelectedPackageFeedId(
        ConfirmationExecutionContext execution,
        OctopusReleaseDto release,
        OctopusSelectedPackageDto selectedPackage,
        out int feedId)
    {
        var sourceFeedId = OctopusImportReleasePackageFeedResolver.GetSourceFeedId(
            execution.Request.Graph,
            execution.CurrentResources,
            release,
            selectedPackage);

        return OctopusImportKubernetesActionMapperSupport.TryResolveFeedId(sourceFeedId, execution.IdMap, out feedId);
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
                    OwnerProjectSourceId = r.OwnerProjectSourceId,
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
                     r.OutcomeState is OctopusImportResourceOutcomeState.Created or OctopusImportResourceOutcomeState.Updated or OctopusImportResourceOutcomeState.Pending))
        {
            // An updated row existed before the import and survives the rollback, so it keeps its id.
            if (resource.OutcomeState != OctopusImportResourceOutcomeState.Updated)
                resource.DestinationId = null;

            resource.OutcomeState = OctopusImportResourceOutcomeState.Failed;

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
        private readonly HashSet<string> _importedBeforeSourceIds = new(StringComparer.OrdinalIgnoreCase);

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
                .Where(r => OctopusResourceMetadata.For(r.Kind).IsCurrentConfiguration(r))
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
                or OctopusImportResourceOutcomeState.Updated
                or OctopusImportResourceOutcomeState.Skipped
                or OctopusImportResourceOutcomeState.Unsupported
                or OctopusImportResourceOutcomeState.Blocked
                or OctopusImportResourceOutcomeState.Failed;
        }

        public OctopusResourceNode FindResource(string sourceId, OctopusResourceKind kind)
            => _resourcesByKey.TryGetValue(Key(sourceId, kind), out var resource) ? resource : null;

        /// <summary>
        /// A destination id must equal the one an earlier confirmation recorded for this resource. A resource without
        /// one inherited "already imported" from its owner, which must itself have been verified in this confirmation.
        /// </summary>
        public bool IsBackedBySourceMap(OctopusResourceNode resource, int? destinationId)
            => destinationId is > 0 ? IdMap.IsPriorDestination(resource, destinationId.Value) : IsOwnerImportedBefore(resource);

        public void MarkImportedBefore(OctopusResourceNode resource) => _importedBeforeSourceIds.Add(resource.SourceId);

        private bool IsOwnerImportedBefore(OctopusResourceNode resource)
        {
            var ownerSourceId = OctopusImportPriorMappingPolicy.OwnerSourceId(resource);

            return !string.IsNullOrWhiteSpace(ownerSourceId) && _importedBeforeSourceIds.Contains(ownerSourceId);
        }

        public bool IsWrittenByImport(OctopusResourceNode resource)
            => OctopusImportVariableOwnership.IsWrittenByImport(resource, sourceId => FindResource(sourceId, OctopusResourceKind.VariableSet), sourceId => _resultsBySourceId.GetValueOrDefault(sourceId)?.PreviewAction);

        public void AddCreatedIfPresent(string sourceId, OctopusResourceKind kind, int destinationId)
        {
            var resource = FindResource(sourceId, kind);
            if (resource != null)
                AddCreated(resource, destinationId);
        }

        public void AddCreatedMappingIfPresent(string sourceId, OctopusResourceKind kind, int destinationId)
        {
            if (destinationId <= 0)
                return;

            var resource = FindResource(sourceId, kind);
            if (resource != null && !IdMap.ContainsSessionMapping(resource))
                IdMap.AddCreated(resource, destinationId);
        }

        public void AddCreated(OctopusResourceNode resource, int destinationId)
        {
            if (!IdMap.ContainsSessionMapping(resource))
                IdMap.AddCreated(resource, destinationId);

            MarkOutcome(resource, OctopusImportResourceOutcomeState.Created, destinationId);
        }

        public void AddReused(OctopusResourceNode resource, int destinationId)
        {
            if (!IdMap.ContainsSessionMapping(resource))
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
