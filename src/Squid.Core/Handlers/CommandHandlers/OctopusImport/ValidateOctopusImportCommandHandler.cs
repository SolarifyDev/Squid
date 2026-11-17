using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Commands.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Handlers.CommandHandlers.OctopusImport;

public class ValidateOctopusImportCommandHandler(
    IOctopusImportSessionDataProvider sessionDataProvider,
    IOctopusImportSessionService sessionService,
    ICurrentUser currentUser,
    IOctopusImportPlanningPipeline planningPipeline,
    IOctopusImportPreviewValidator previewValidator,
    IOctopusImportExportSecretStore secretStore = null,
    IOctopusExportSecretsAccessor secretsAccessor = null)
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

        await secretStore.ApplyToAsync(secretsAccessor, command.SessionId, destinationSpaceId, cancellationToken).ConfigureAwait(false);

        // The user's selection is an input to the server's plan: the rows it decides come from that plan, never from the client.
        var selections = OctopusImportChoiceSelections.From(command.PreviewPlan);
        var chosenProjectIds = OctopusImportSourceSummaries.ChosenProjectIds(session.SourceSummaryJson);

        OctopusImportPlanningSnapshot snapshot;
        try
        {
            snapshot = await planningPipeline.BuildPreviewAsync(session.TemporaryUploadPath, destinationSpaceId, selections, chosenProjectIds, cancellationToken).ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            return BadRequest(command, state, command.PreviewPlan, new OctopusImportValidationResultDto(), ex.Message);
        }

        var previewPlan = OctopusImportRedaction.RedactDto(OctopusImportServerOwnedRows.MergeServerOwnedRows(command.PreviewPlan, snapshot.PreviewPlan));
        var validation = OctopusImportRedaction.RedactDto(previewValidator.Validate(
            snapshot.Graph,
            snapshot.DependencyPlan,
            snapshot.Conflicts,
            previewPlan,
            snapshot.PreviewPlan));

        if (previewPlan.HasBlockers || validation.HasBlockers)
        {
            await ClearValidatedPlanAsync(command, state, cancellationToken).ConfigureAwait(false);

            return BadRequest(command, state, previewPlan, validation, "Octopus import validation produced blocking diagnostics.");
        }

        var validatedPlan = new OctopusImportValidatedPlanDto
        {
            PreviewPlan = previewPlan,
            Validation = validation,
            SelectedProjectIds = chosenProjectIds?.ToList()
        };

        var responseSession = await StoreValidatedPlanAsync(command, state, JsonSerializer.Serialize(validatedPlan, JsonOptions), cancellationToken).ConfigureAwait(false);

        return new ValidateOctopusImportResponse
        {
            Code = HttpStatusCode.OK,
            Data = new ValidateOctopusImportResponseData
            {
                Session = responseSession,
                PreviewPlan = previewPlan,
                Validation = validation,
                BlockerSummary = OctopusImportBlockerSummaryBuilder.Build(previewPlan, validation)
            }
        };
    }

    // A validation in Validated (the user changed what to import after validating) replaces the plan confirmation imports.
    private async Task<OctopusImportSessionDto> StoreValidatedPlanAsync(ValidateOctopusImportCommand command, OctopusImportSessionState state, string validatedPlanJson, CancellationToken ct)
    {
        var spaceId = GetSpaceId(command);

        if (state == OctopusImportSessionState.Validated) return await sessionService.ReplaceValidatedPlanAsync(command.SessionId, spaceId, validatedPlanJson, ct).ConfigureAwait(false);

        return await sessionService.UpdatePayloadAndTransitionAsync(command.SessionId, spaceId, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated, validatedPlanJson: validatedPlanJson, ct: ct).ConfigureAwait(false);
    }

    // A blocked validation of a Validated session clears the plan it validated before: confirmation never imports a plan the user has
    // since changed, and refuses until a validation passes again.
    private async Task ClearValidatedPlanAsync(ValidateOctopusImportCommand command, OctopusImportSessionState state, CancellationToken ct)
    {
        if (state != OctopusImportSessionState.Validated) return;

        await sessionService.ReplaceValidatedPlanAsync(command.SessionId, GetSpaceId(command), null, ct).ConfigureAwait(false);
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

        var blockerSummary = OctopusImportBlockerSummaryBuilder.Build(previewPlan, validation);

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
                Validation = validation,
                BlockerSummary = blockerSummary
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
