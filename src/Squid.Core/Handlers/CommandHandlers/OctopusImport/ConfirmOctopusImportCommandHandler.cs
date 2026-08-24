using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ConfirmOctopusImportCommandHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
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
            return new ConfirmOctopusImportResponse
            {
                Code = HttpStatusCode.OK,
                Data = new ConfirmOctopusImportResponseData
                {
                    Session = await sessionService.GetSessionAsync(command.SessionId, destinationSpaceId, cancellationToken).ConfigureAwait(false)
                }
            };

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
                Session = resultSession
            }
        };
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(ConfirmOctopusImportCommand command, CancellationToken ct)
    {
        var destinationSpaceId = GetSpaceId(command);
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import confirmation requires an authenticated user.");

        var session = await sessionDataProvider
            .GetSessionAsync(command.SessionId, currentUser.Id.Value, destinationSpaceId, ct)
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
        => new()
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
                }
            }
        };

    private static int GetSpaceId(ConfirmOctopusImportCommand command)
        => command.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import confirmation requires destination space context.");

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
