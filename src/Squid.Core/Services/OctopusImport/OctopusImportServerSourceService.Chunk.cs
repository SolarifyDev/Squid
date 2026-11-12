using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed partial class OctopusImportServerSourceService
{
    // The cursor is persisted after the chunk whether it succeeded or not: pages already staged are not fetched again.
    private async Task<OctopusHistoryFetchProgressDto> RunClaimedChunkAsync(OctopusHistoryFetchRequest request, OctopusImportSession session, OctopusImportHistoryFetchDto cursor, CancellationToken ct)
    {
        try
        {
            var context = await BuildContextAsync(request, session, cursor, ct).ConfigureAwait(false);
            var progress = await fetcher.FetchChunkAsync(context, ct).ConfigureAwait(false);

            await ReleaseChunkAsync(request, cursor, ct).ConfigureAwait(false);

            return progress;
        }
        catch
        {
            await TryReleaseChunkAsync(request, cursor).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<OctopusHistoryFetchContext> BuildContextAsync(OctopusHistoryFetchRequest request, OctopusImportSession session, OctopusImportHistoryFetchDto cursor, CancellationToken ct)
        => new()
        {
            ServerUrl = request.ServerUrl,
            ApiKey = request.ApiKey,
            SessionDirectory = Path.GetDirectoryName(Path.GetFullPath(session.TemporaryUploadPath)),
            Cursor = cursor,
            PriorMappings = await sourceMap.LoadAsync(request.SpaceId, ct).ConfigureAwait(false),
            ExportProjectIds = cursor.ProjectIds.Count == 0 ? await ExportProjectIdsAsync(session.TemporaryUploadPath, ct).ConfigureAwait(false) : []
        };

    // Only the first chunk needs the uploaded export: it decides the project scope, which the cursor then keeps.
    private async Task<IReadOnlyList<string>> ExportProjectIdsAsync(string temporaryUploadPath, CancellationToken ct)
    {
        if (OctopusImportSourceReader.IsHistoryBundle(temporaryUploadPath) || !File.Exists(temporaryUploadPath)) return [];

        var extraction = await sourceReader.ReadAsync(temporaryUploadPath, ct).ConfigureAwait(false);

        return extraction.Documents.Where(d => d.Classification.Kind == OctopusDocumentKind.Project && !d.Classification.IsHistoricalSnapshot).Select(d => d.Classification.SourceId).Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
    }

    private async Task ReleaseChunkAsync(OctopusHistoryFetchRequest request, OctopusImportHistoryFetchDto cursor, CancellationToken ct)
    {
        cursor.LeaseExpiresAt = null;

        await sessionService.UpdateHistoryFetchAsync(request.SessionId, request.SpaceId, cursor, cursor.ChunkId, ct).ConfigureAwait(false);
    }

    // Runs uncancellable after a failure; a failed release is logged and never masks the original failure.
    private async Task TryReleaseChunkAsync(OctopusHistoryFetchRequest request, OctopusImportHistoryFetchDto cursor)
    {
        try
        {
            await ReleaseChunkAsync(request, cursor, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not release the history fetch chunk of Octopus import session {SessionId}", request.SessionId);
        }
    }
}
