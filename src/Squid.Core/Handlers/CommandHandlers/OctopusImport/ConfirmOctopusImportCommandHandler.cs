using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ConfirmOctopusImportCommandHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
    IAuthorizationService authorizationService,
    IOctopusImportPlanningPipeline planningPipeline,
    IOctopusImportConfirmationOrchestrator confirmationOrchestrator)
    : ICommandHandler<ConfirmOctopusImportCommand, ConfirmOctopusImportResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ConfirmOctopusImportResponse> Handle(
        IReceiveContext<ConfirmOctopusImportCommand> context,
        CancellationToken cancellationToken)
    {
        var command = context.Message;
        var destinationSpaceId = GetSpaceId(command);
        var session = await GetOwnedSessionAsync(command, cancellationToken).ConfigureAwait(false);
        var state = ParseState(session.State);

        if (OctopusImportSessionStateMachine.IsTerminal(state) || state == OctopusImportSessionState.Importing)
        {
            var currentSession = await sessionService
                .GetSessionAsync(command.SessionId, destinationSpaceId, cancellationToken)
                .ConfigureAwait(false);

            return new ConfirmOctopusImportResponse
            {
                Code = HttpStatusCode.OK,
                Data = new ConfirmOctopusImportResponseData
                {
                    Session = currentSession,
                    BlockerSummary = OctopusImportBlockerSummaryBuilder.Build(currentSession?.Result)
                }
            };
        }

        if (state != OctopusImportSessionState.Validated)
            return BadRequest(command, state, "Octopus import confirmation requires a validated session.");

        if (string.IsNullOrWhiteSpace(session.TemporaryUploadPath) || !File.Exists(session.TemporaryUploadPath))
            return BadRequest(command, state, "Octopus import session does not have an available temporary upload.");

        OctopusImportValidatedPlanDto validatedPlan;
        try
        {
            validatedPlan = DeserializeValidatedPlan(session.ValidatedPlanJson);
        }
        catch (JsonException ex)
        {
            return BadRequest(command, state, $"Octopus import session contains an invalid validated plan: {ex.Message}");
        }

        if (validatedPlan?.PreviewPlan == null)
            return BadRequest(command, state, "Octopus import session does not have a validated preview plan.");

        OctopusImportPlanningSnapshot snapshot;
        try
        {
            snapshot = await planningPipeline
                .BuildPreviewAsync(session.TemporaryUploadPath, destinationSpaceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            return BadRequest(command, state, ex.Message);
        }

        await EnsureImportPermissionsAsync(
                validatedPlan.PreviewPlan,
                snapshot.Graph,
                destinationSpaceId,
                cancellationToken)
            .ConfigureAwait(false);

        var resultSession = await confirmationOrchestrator
            .ConfirmAsync(
                new OctopusImportConfirmationRequest(
                    command.SessionId,
                    destinationSpaceId,
                    snapshot.Graph,
                    snapshot.DependencyPlan,
                    validatedPlan.PreviewPlan),
                cancellationToken)
            .ConfigureAwait(false);

        return new ConfirmOctopusImportResponse
        {
            Code = HttpStatusCode.OK,
            Data = new ConfirmOctopusImportResponseData
            {
                Session = resultSession,
                BlockerSummary = OctopusImportBlockerSummaryBuilder.Build(resultSession?.Result)
            }
        };
    }

    private async Task EnsureImportPermissionsAsync(
        OctopusImportPreviewPlanDto previewPlan,
        OctopusResourceGraph graph,
        int destinationSpaceId,
        CancellationToken ct)
    {
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import confirmation requires an authenticated user.");

        var currentResources = graph.Resources
            .GroupBy(r => r.SourceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var permissions = previewPlan.Resources
            .Where(r => r.PreviewAction == OctopusImportPreviewAction.Create)
            .Select(r => ResolvePermission(r, currentResources))
            .Where(permission => permission.HasValue)
            .Select(permission => permission.Value)
            .Distinct();

        foreach (var permission in permissions)
        {
            await authorizationService
                .EnsurePermissionAsync(
                    new PermissionCheckRequest
                    {
                        UserId = currentUser.Id.Value,
                        Permission = permission,
                        SpaceId = destinationSpaceId
                    },
                    ct)
                .ConfigureAwait(false);
        }
    }

    private static Permission? ResolvePermission(
        OctopusImportResourceResultDto previewResource,
        IReadOnlyDictionary<string, OctopusResourceNode> currentResources)
    {
        if (!currentResources.TryGetValue(previewResource.SourceId, out var resource))
            return null;

        return resource.Kind switch
        {
            OctopusResourceKind.ProjectGroup or OctopusResourceKind.Project => Permission.ProjectCreate,
            OctopusResourceKind.Environment => Permission.EnvironmentCreate,
            OctopusResourceKind.Lifecycle => Permission.LifecycleCreate,
            OctopusResourceKind.Feed => Permission.FeedEdit,
            OctopusResourceKind.Account => Permission.AccountCreate,
            OctopusResourceKind.Channel => ResolveChannelPermission(resource),
            OctopusResourceKind.VariableSet => Permission.VariableEdit,
            OctopusResourceKind.DeploymentProcess => Permission.ProcessEdit,
            OctopusResourceKind.Release => Permission.ReleaseCreate,
            _ => null
        };
    }

    private static Permission ResolveChannelPermission(OctopusResourceNode resource)
    {
        return resource.GetSource<OctopusChannelDto>()?.IsDefault == true
            ? Permission.ChannelEdit
            : Permission.ChannelCreate;
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(ConfirmOctopusImportCommand command, CancellationToken ct)
    {
        var destinationSpaceId = GetSpaceId(command);
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import confirmation requires an authenticated user.");

        var session = await sessionDataProvider
            .GetSessionNoTrackingAsync(command.SessionId, currentUser.Id.Value, destinationSpaceId, ct)
            .ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(command.SessionId);

        return session;
    }

    private static OctopusImportValidatedPlanDto DeserializeValidatedPlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        return JsonSerializer.Deserialize<OctopusImportValidatedPlanDto>(json, JsonOptions);
    }

    private static ConfirmOctopusImportResponse BadRequest(
        ConfirmOctopusImportCommand command,
        OctopusImportSessionState state,
        string message)
    {
        var diagnostic = new OctopusImportDiagnosticDto
        {
            Severity = OctopusImportCompatibilitySeverity.Blocker,
            Code = OctopusImportConfirmationDiagnosticCodes.ConfirmationRequiresValidatedSession,
            Message = message
        };

        return new ConfirmOctopusImportResponse
        {
            Code = HttpStatusCode.BadRequest,
            Msg = message,
            Data = new ConfirmOctopusImportResponseData
            {
                Session = new OctopusImportSessionDto
                {
                    SessionId = command.SessionId,
                    DestinationSpaceId = command.SpaceId ?? 0,
                    State = state
                },
                BlockerSummary = OctopusImportBlockerSummaryBuilder.Build([diagnostic])
            }
        };
    }

    private static int GetSpaceId(ConfirmOctopusImportCommand command)
        => command.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import confirmation requires destination space context.");

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
