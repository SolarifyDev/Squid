using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public partial class OctopusImportSessionService
{
    public const int MaxListedSessions = 20;

    public async Task<IReadOnlyList<OctopusImportResumableSessionDto>> ListResumableSessionsAsync(int destinationSpaceId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await _dataProvider.GetResumableSessionsNoTrackingAsync(GetCurrentUserId(), destinationSpaceId, now, MaxListedSessions, ct).ConfigureAwait(false);

        return rows.Select(row => MapResumable(row, now)).Where(dto => dto != null).ToList();
    }

    // Built field by field, never by copying the history fetch: the key's fingerprint, the Octopus user and the cursor stay on the server.
    private OctopusImportResumableSessionDto MapResumable(OctopusImportResumableSessionRow row, DateTimeOffset now)
    {
        var state = ParseState(row.State);
        var summary = OctopusImportSourceSummaries.Parse(row.SourceSummaryJson) ?? new OctopusImportSourceSummaryDto();
        var step = OctopusImportResumeSteps.Of(state, summary, row.HasUpload, row.HasConfirmationFailure);

        if (step == null) return null;

        var sourceKind = OctopusImportResumeSteps.SourceKindOf(summary);
        var fetch = sourceKind == OctopusImportSourceKind.OctopusServer ? summary.HistoryFetch : null;

        return new OctopusImportResumableSessionDto
        {
            SessionId = row.SessionId,
            State = state,
            ResumeStep = step.Value,
            SourceKind = sourceKind,
            FileName = sourceKind == OctopusImportSourceKind.ExportFile ? summary.FileName : null,
            ServerUrl = fetch?.ServerUrl,
            ServerHost = fetch?.ServerHost,
            OctopusSpaceId = fetch?.OctopusSpaceId,
            SelectedProjectCount = summary.SelectedProjectIds?.Count,
            SelectedProjectNames = fetch?.ChosenProjectNames?.ToList() ?? [],
            ReadProgress = fetch == null ? null : OctopusRestHistoryFetcher.Progress(fetch),
            ReadActive = fetch?.LeaseExpiresAt > now,
            CreatedAt = row.CreatedDate,
            UpdatedAt = row.LastModifiedDate,
            LastStateChangedAt = row.LastStateChangedAt,
            ExpiresAt = row.ExpiresAt,
            MaxExpiresAt = OctopusImportSessionExpiry.Deadline(row.CreatedDate, _temporaryUploadSettings.MaxSessionAge)
        };
    }
}
