using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

/// <summary>The Octopus server (REST API) as an import source: probe a server, open a session for it, fetch its history.</summary>
public interface IOctopusImportServerSourceService : IScopedDependency
{
    Task<OctopusServerProbeDto> ProbeAsync(string serverUrl, string apiKey, CancellationToken ct = default);

    Task<OctopusImportSessionDto> ConnectAsync(int destinationSpaceId, CancellationToken ct = default);

    Task<OctopusHistoryFetchProgressDto> FetchHistoryAsync(OctopusHistoryFetchRequest request, CancellationToken ct = default);
}

public sealed partial class OctopusImportServerSourceService(IOctopusImportSessionDataProvider sessionDataProvider, IOctopusImportSessionService sessionService, ICurrentUser currentUser, IOctopusImportTemporaryUploadStore uploadStore, IOctopusRestHistoryFetcher fetcher, IOctopusImportSourceMapDataProvider sourceMap, OctopusRestOptions options = null) : IOctopusImportServerSourceService
{
    public const string ServerSourceFormat = "OctopusServer";

    /// <summary>How long a running chunk holds the session beyond its time budget, so a crashed chunk frees it eventually.</summary>
    public static readonly TimeSpan LeaseMargin = TimeSpan.FromMinutes(5);

    private readonly OctopusRestOptions _options = options ?? OctopusRestOptions.FromEnvironment();

    public Task<OctopusServerProbeDto> ProbeAsync(string serverUrl, string apiKey, CancellationToken ct = default) => fetcher.ProbeAsync(serverUrl, apiKey, ct);

    public async Task<OctopusImportSessionDto> ConnectAsync(int destinationSpaceId, CancellationToken ct = default)
    {
        var summary = new OctopusImportSourceSummaryDto { FileName = OctopusImportSourceReader.HistoryBundleFileName, ContentType = "application/zip", DetectedFormat = ServerSourceFormat, HistoryFetch = new OctopusImportHistoryFetchDto { Phase = OctopusHistoryFetchPhase.Catalog } };
        var session = await sessionService.CreateSessionAsync(destinationSpaceId, summary, ct).ConfigureAwait(false);
        var directory = uploadStore.GetSessionDirectory(session.SessionId);

        Directory.CreateDirectory(directory);

        // The bundle does not exist until the fetch finishes; every "temporary upload missing" gate holds until then,
        // and the registered path makes the cleanup job purge the session directory like any upload.
        var upload = new OctopusImportTemporaryUpload(Path.Combine(directory, OctopusImportSourceReader.HistoryBundleFileName), 0);

        return await sessionService.RegisterTemporaryUploadAsync(session.SessionId, destinationSpaceId, upload, ct).ConfigureAwait(false);
    }

    public async Task<OctopusHistoryFetchProgressDto> FetchHistoryAsync(OctopusHistoryFetchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await LoadUploadedSessionAsync(request, ct).ConfigureAwait(false);
        var summary = OctopusImportSourceSummaries.Parse(session.SourceSummaryJson);

        EnsureServerSession(summary, request.SessionId);

        var cursor = summary.HistoryFetch ?? new OctopusImportHistoryFetchDto { Phase = OctopusHistoryFetchPhase.Catalog };

        if (cursor.Done) return OctopusRestHistoryFetcher.Progress(cursor);

        PinServer(cursor, request);
        EnsureNoActiveChunk(cursor, request.SessionId);

        await ClaimChunkAsync(request, cursor, ct).ConfigureAwait(false);

        return await RunClaimedChunkAsync(request, session, cursor, ct).ConfigureAwait(false);
    }

    private async Task<OctopusImportSession> LoadUploadedSessionAsync(OctopusHistoryFetchRequest request, CancellationToken ct)
    {
        if (currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import history fetch requires an authenticated user.");

        var session = await sessionDataProvider.GetSessionNoTrackingAsync(request.SessionId, currentUser.Id.Value, request.SpaceId, ct).ConfigureAwait(false) ?? throw new OctopusImportSessionNotFoundException(request.SessionId);
        var state = Enum.Parse<OctopusImportSessionState>(session.State, ignoreCase: true);

        if (state != OctopusImportSessionState.Uploaded)
            throw new OctopusImportSessionStateTransitionException(state, OctopusImportSessionState.Uploaded);

        if (string.IsNullOrWhiteSpace(session.TemporaryUploadPath))
            throw new OctopusImportSessionNotFoundException(request.SessionId);

        return session;
    }

    // One session, one source: a session opened from an export file never reads from an Octopus server.
    public static void EnsureServerSession(OctopusImportSourceSummaryDto summary, Guid sessionId)
    {
        if (!string.Equals(summary?.DetectedFormat, ServerSourceFormat, StringComparison.Ordinal))
            throw new OctopusImportSessionNotServerSourceException(sessionId);
    }

    // The first chunk pins the server and the Octopus space; a later chunk naming another one is refused.
    public static void PinServer(OctopusImportHistoryFetchDto cursor, OctopusHistoryFetchRequest request)
    {
        var host = ServerHostOf(request.ServerUrl);
        var space = string.IsNullOrWhiteSpace(request.OctopusSpaceId) ? null : request.OctopusSpaceId.Trim();

        if (cursor.ServerHost != null && !string.Equals(cursor.ServerHost, host, StringComparison.OrdinalIgnoreCase))
            throw new OctopusImportHistoryServerMismatchException("server");

        if (cursor.OctopusSpaceId != null && space != null && !string.Equals(cursor.OctopusSpaceId, space, StringComparison.OrdinalIgnoreCase))
            throw new OctopusImportHistoryServerMismatchException("space");

        cursor.ServerHost ??= host;
        cursor.OctopusSpaceId ??= space;
    }

    public static string ServerHostOf(string serverUrl) => Uri.TryCreate(serverUrl?.Trim(), UriKind.Absolute, out var uri) ? uri.Authority.ToLowerInvariant() : null;

    private static void EnsureNoActiveChunk(OctopusImportHistoryFetchDto cursor, Guid sessionId)
    {
        if (cursor.LeaseExpiresAt > DateTimeOffset.UtcNow)
            throw new OctopusImportSessionConcurrencyException(sessionId);
    }

    private async Task ClaimChunkAsync(OctopusHistoryFetchRequest request, OctopusImportHistoryFetchDto cursor, CancellationToken ct)
    {
        var previousChunkId = cursor.ChunkId;

        cursor.ChunkId = Guid.NewGuid();
        cursor.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(_options.ChunkDuration).Add(LeaseMargin);

        await sessionService.UpdateHistoryFetchAsync(request.SessionId, request.SpaceId, cursor, previousChunkId, ct).ConfigureAwait(false);
    }
}
