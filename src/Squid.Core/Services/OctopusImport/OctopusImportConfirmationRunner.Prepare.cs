using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed partial class OctopusImportConfirmationRunner
{
    public const string SupersededSinceQueuedMessage = "The session changed after this import was queued, so nothing was imported. Import again to import the current validated plan.";

    public const string SelectionChangedMessage = "The projects to import changed after the preview was validated, so nothing was imported. Validate the preview again, then import.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    // Loaded by the owner passed in, never by whoever the scope's current user happens to be.
    private async Task LoadOwnedSessionAsync(CancellationToken ct)
    {
        var session = await sessionDataProvider.GetSessionNoTrackingAsync(_ctx.SessionId, _ctx.OwnerUserId, _ctx.SpaceId, ct).ConfigureAwait(false);

        _ctx.Session = session ?? throw new OctopusImportSessionNotFoundException(_ctx.SessionId);
        _ctx.State = ParseState(session.State);
    }

    private async Task EnsureConfirmableAsync(CancellationToken ct)
    {
        EnsureValidated();
        EnsureStillTheAdmittedPlan();
        EnsureWithinMaxAge();
        await EnsureSourceAvailableAsync(ct).ConfigureAwait(false);
        ReadValidatedPlan();
        EnsurePlanMatchesSelection();

        await EnsureExportSecretsReadableAsync(ct).ConfigureAwait(false);
    }

    private void EnsureValidated()
    {
        if (_ctx.State != OctopusImportSessionState.Validated)
            throw new OctopusImportConfirmationRejectedException("Octopus import confirmation requires a validated session.");
    }

    // A queued run imports the plan the user confirmed, never one validated (or recovered) after Import was pressed.
    private void EnsureStillTheAdmittedPlan()
    {
        if (_ctx.AdmittedDataVersion != null && !_ctx.AdmittedDataVersion.AsSpan().SequenceEqual(_ctx.Session.DataVersion))
            throw new OctopusImportConfirmationRejectedException(SupersededSinceQueuedMessage, OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded);
    }

    // Refused before planning or queueing, as SessionExpired (409): a queued run would otherwise only fail later as a generic background failure.
    private void EnsureWithinMaxAge()
    {
        var maxAge = temporaryUploadSettings.MaxSessionAge;

        if (OctopusImportSessionExpiry.IsOverAge(_ctx.Session.CreatedDate, maxAge, DateTimeOffset.UtcNow))
            throw new OctopusImportSessionExpiredException(_ctx.SessionId, OctopusImportSessionExpiry.Deadline(_ctx.Session.CreatedDate, maxAge), maxAge);
    }

    // Any server may run the confirmation: the source is read from the session's stored copy when this server has none.
    private async Task EnsureSourceAvailableAsync(CancellationToken ct)
    {
        _ctx.SourcePath = await uploadStore.MaterializeAsync(_ctx.Session, ct).ConfigureAwait(false);

        if (_ctx.SourcePath == null)
            throw new OctopusImportConfirmationRejectedException("Octopus import session does not have an available temporary upload.");
    }

    private void ReadValidatedPlan()
    {
        _ctx.ValidatedPlan = DeserializeValidatedPlan(_ctx.Session.ValidatedPlanJson);

        if (_ctx.ValidatedPlan?.PreviewPlan == null)
            throw new OctopusImportConfirmationRejectedException("Octopus import session does not have a validated preview plan.");
    }

    // A validation that read one selection may store its plan after the selection changed: never import a plan for another selection.
    private void EnsurePlanMatchesSelection()
    {
        if (!IsSameSelection(_ctx.ValidatedPlan.SelectedProjectIds, OctopusImportSourceSummaries.ChosenProjectIds(_ctx.Session.SourceSummaryJson)))
            throw new OctopusImportConfirmationRejectedException(SelectionChangedMessage, OctopusImportConfirmationDiagnosticCodes.ConfirmationSuperseded);
    }

    private static bool IsSameSelection(IReadOnlyCollection<string> validated, IReadOnlyCollection<string> current)
        => validated == null || current == null ? validated == null && current == null : validated.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(current);

    // An envelope that the current master key cannot read was written under a key that did decrypt it, so the
    // plan may have been validated with the secrets; confirming without them would import a different result
    // and end the session terminally. Refuse instead: the session stays Validated, so restoring the master key
    // and retrying works, and expiry clears the password otherwise. A password that was never retained
    // (NotRetained) is not refused: it can never become readable and was validated without secrets.
    private async Task EnsureExportSecretsReadableAsync(CancellationToken ct)
    {
        await secretStore.ApplyToAsync(secretsAccessor, _ctx.SessionId, _ctx.SpaceId, ct).ConfigureAwait(false);

        if (secretsAccessor?.Secrets.IsUnreadable == true)
            throw new OctopusImportConfirmationRejectedException(OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailableMessage, OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable);
    }

    private async Task BuildSnapshotAsync(CancellationToken ct)
    {
        try
        {
            _ctx.Snapshot = await planningPipeline.BuildPreviewAsync(_ctx.SourcePath, _ctx.SpaceId, chosenProjectIds: OctopusImportSourceSummaries.ChosenProjectIds(_ctx.Session.SourceSummaryJson), ct: ct).ConfigureAwait(false);
        }
        catch (OctopusArchiveExtractionException ex)
        {
            throw new OctopusImportConfirmationRejectedException(ex.Message);
        }
    }

    private static OctopusImportValidatedPlanDto DeserializeValidatedPlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try
        {
            return JsonSerializer.Deserialize<OctopusImportValidatedPlanDto>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new OctopusImportConfirmationRejectedException($"Octopus import session contains an invalid validated plan: {ex.Message}");
        }
    }

    private static OctopusImportSessionState ParseState(string state)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed) ? parsed : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
}
