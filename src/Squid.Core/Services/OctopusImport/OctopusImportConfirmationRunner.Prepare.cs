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
        EnsureTemporaryUpload();
        ReadValidatedPlan();

        await EnsureExportSecretsReadableAsync(ct).ConfigureAwait(false);
    }

    private void EnsureValidated()
    {
        if (_ctx.State != OctopusImportSessionState.Validated)
            throw new OctopusImportConfirmationRejectedException("Octopus import confirmation requires a validated session.");
    }

    private void EnsureTemporaryUpload()
    {
        if (string.IsNullOrWhiteSpace(_ctx.Session.TemporaryUploadPath) || !File.Exists(_ctx.Session.TemporaryUploadPath))
            throw new OctopusImportConfirmationRejectedException("Octopus import session does not have an available temporary upload.");
    }

    private void ReadValidatedPlan()
    {
        _ctx.ValidatedPlan = DeserializeValidatedPlan(_ctx.Session.ValidatedPlanJson);

        if (_ctx.ValidatedPlan?.PreviewPlan == null)
            throw new OctopusImportConfirmationRejectedException("Octopus import session does not have a validated preview plan.");
    }

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
            _ctx.Snapshot = await planningPipeline.BuildPreviewAsync(_ctx.Session.TemporaryUploadPath, _ctx.SpaceId, ct).ConfigureAwait(false);
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
