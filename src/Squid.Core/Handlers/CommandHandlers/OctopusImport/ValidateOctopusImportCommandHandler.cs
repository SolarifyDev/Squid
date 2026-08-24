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

public class ValidateOctopusImportCommandHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
    IOctopusImportPlanningPipeline planningPipeline,
    IOctopusImportPreviewValidator previewValidator)
    : ICommandHandler<ValidateOctopusImportCommand, ValidateOctopusImportResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ValidateOctopusImportResponse> Handle(
        IReceiveContext<ValidateOctopusImportCommand> context,
        CancellationToken cancellationToken)
    {
        var command = context.Message;
        var destinationSpaceId = GetSpaceId(command);
        var session = await GetOwnedSessionAsync(command, cancellationToken).ConfigureAwait(false);
        var state = ParseState(session.State);
        EnsureValidateAllowed(state);

        if (string.IsNullOrWhiteSpace(session.TemporaryUploadPath) || !File.Exists(session.TemporaryUploadPath))
            return BadRequest(command, state, command.PreviewPlan, new OctopusImportValidationResultDto(), "Octopus import session does not have an available temporary upload.");

        OctopusImportPlanningSnapshot snapshot;
        try
        {
            snapshot = await planningPipeline
                .BuildPreviewAsync(session.TemporaryUploadPath, destinationSpaceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            return BadRequest(command, state, command.PreviewPlan, new OctopusImportValidationResultDto(), ex.Message);
        }

        var previewPlan = OctopusImportRedaction.RedactDto(command.PreviewPlan ?? snapshot.PreviewPlan);
        var validation = OctopusImportRedaction.RedactDto(previewValidator.Validate(
            snapshot.Graph,
            snapshot.DependencyPlan,
            snapshot.Conflicts,
            previewPlan));

        if (previewPlan.HasBlockers || validation.HasBlockers)
            return BadRequest(command, state, previewPlan, validation, "Octopus import validation produced blocking diagnostics.");

        var validatedPlan = new OctopusImportValidatedPlanDto
        {
            PreviewPlan = previewPlan,
            Validation = validation
        };

        var responseSession = state == OctopusImportSessionState.Previewed
            ? await sessionService
                .UpdatePayloadAndTransitionAsync(
                    command.SessionId,
                    destinationSpaceId,
                    OctopusImportSessionState.Previewed,
                    OctopusImportSessionState.Validated,
                    validatedPlanJson: JsonSerializer.Serialize(validatedPlan, JsonOptions),
                    ct: cancellationToken)
                .ConfigureAwait(false)
            : await sessionService.GetSessionAsync(command.SessionId, destinationSpaceId, cancellationToken).ConfigureAwait(false);

        return new ValidateOctopusImportResponse
        {
            Code = HttpStatusCode.OK,
            Data = new ValidateOctopusImportResponseData
            {
                Session = responseSession,
                PreviewPlan = previewPlan,
                Validation = validation
            }
        };
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(ValidateOctopusImportCommand command, CancellationToken ct)
    {
        var destinationSpaceId = GetSpaceId(command);
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import validation requires an authenticated user.");

        var session = await sessionDataProvider
            .GetSessionAsync(command.SessionId, currentUser.Id.Value, destinationSpaceId, ct)
            .ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(command.SessionId);

        return session;
    }

    private static void EnsureValidateAllowed(OctopusImportSessionState state)
    {
        if (state is OctopusImportSessionState.Previewed or OctopusImportSessionState.Validated)
            return;

        throw new OctopusImportSessionStateTransitionException(state, OctopusImportSessionState.Validated);
    }

    private static ValidateOctopusImportResponse BadRequest(
        ValidateOctopusImportCommand command,
        OctopusImportSessionState state,
        OctopusImportPreviewPlanDto previewPlan,
        OctopusImportValidationResultDto validation,
        string message)
    {
        validation ??= new OctopusImportValidationResultDto();
        if (!validation.Diagnostics.Any(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker))
        {
            validation.Diagnostics.Add(OctopusImportRedaction.RedactDiagnostic(new OctopusImportDiagnosticDto
            {
                Severity = OctopusImportCompatibilitySeverity.Blocker,
                Code = "OctopusImport.Validation.Blocked",
                Message = message
            }));
        }

        return new ValidateOctopusImportResponse
        {
            Code = HttpStatusCode.BadRequest,
            Msg = message,
            Data = new ValidateOctopusImportResponseData
            {
                Session = new OctopusImportSessionDto
                {
                    SessionId = command.SessionId,
                    DestinationSpaceId = command.SpaceId ?? 0,
                    State = state
                },
                PreviewPlan = previewPlan,
                Validation = validation
            }
        };
    }

    private static int GetSpaceId(ValidateOctopusImportCommand command)
        => command.SpaceId
           ?? throw new UnauthorizedAccessException("Octopus import validation requires destination space context.");

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
