using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed partial class OctopusImportServerSourceService
{
    // The cursor is persisted after the chunk whether it succeeded or not: pages already staged are not fetched again.
    private async Task<OctopusHistoryFetchProgressDto> RunClaimedChunkAsync(OctopusHistoryFetchRequest request, OctopusImportSession session, OctopusImportHistoryFetchDto cursor, IReadOnlyCollection<string> chosenProjectIds, CancellationToken ct)
    {
        try
        {
            var context = await BuildContextAsync(request, session, cursor, chosenProjectIds, ct).ConfigureAwait(false);
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

    // The project selection is the claimed session's, never the request's.
    private async Task<OctopusHistoryFetchContext> BuildContextAsync(OctopusHistoryFetchRequest request, OctopusImportSession session, OctopusImportHistoryFetchDto cursor, IReadOnlyCollection<string> chosenProjectIds, CancellationToken ct)
        => new()
        {
            ServerUrl = request.ServerUrl,
            ApiKey = request.ApiKey,
            SessionDirectory = Path.GetDirectoryName(Path.GetFullPath(session.TemporaryUploadPath)),
            Cursor = cursor,
            PriorMappings = await sourceMap.LoadKindsAsync(request.SpaceId, OctopusRestHistoryFetcher.PriorMappingKinds, ct).ConfigureAwait(false),
            ChosenProjectIds = chosenProjectIds
        };

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
