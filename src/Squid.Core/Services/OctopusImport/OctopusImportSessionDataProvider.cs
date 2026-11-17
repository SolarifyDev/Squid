using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportSessionDataProvider : IScopedDependency
{
    Task AddSessionAsync(OctopusImportSession session, bool forceSave = true, CancellationToken ct = default);

    Task<OctopusImportSession> GetSessionAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default);

    Task<OctopusImportSession> GetSessionNoTrackingAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default);

    Task UpdateSessionAsync(
        OctopusImportSession session,
        byte[] expectedDataVersion,
        bool forceSave = true,
        CancellationToken ct = default);

    Task<int> TransitionStateAsync(
        Guid sessionId,
        int ownerUserId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        CancellationToken ct = default);

    Task<int> TransitionStateAsync(
        Guid sessionId,
        int ownerUserId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        byte[] expectedDataVersion,
        CancellationToken ct = default);

    /// <summary>
    /// Moves a Validated session holding <paramref name="expectedDataVersion"/> to Importing, atomically; a session created at or before
    /// <paramref name="createdAfter"/> (past its maximum age) is never admitted.
    /// </summary>
    Task<bool> TryStartConfirmationAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, byte[] expectedDataVersion, DateTimeOffset createdAfter, CancellationToken ct = default);

    Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>
    /// Expires every session created at or before <paramref name="createdBefore"/> (past its maximum age) that is not Importing or already
    /// closed, recording <paramref name="resultJson"/> as the reason; it clears the export password and makes the upload eligible for cleanup.
    /// </summary>
    Task<int> ExpireOverAgeSessionsAsync(DateTimeOffset now, DateTimeOffset createdBefore, string resultJson, CancellationToken ct = default);

    /// <summary>
    /// The history fetch (server host, Octopus space, version — never the key) of the newest import session of the space that read the
    /// given Octopus installation, of any user; null when no session records it.
    /// </summary>
    Task<OctopusImportHistoryFetchDto> GetLatestHistoryFetchAsync(int destinationSpaceId, string installationId, CancellationToken ct = default);

    Task<int> MarkInterruptedImportsFailedAsync(
        DateTimeOffset now,
        DateTimeOffset staleBefore,
        TimeSpan failedRetentionPeriod,
        CancellationToken ct = default);

    Task<IReadOnlyList<OctopusImportTemporaryUploadCleanupCandidate>> GetTemporaryUploadCleanupCandidatesAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default);

    Task<int> MarkTemporaryUploadCleanupFailedAsync(
        Guid sessionId,
        string temporaryUploadPath,
        string error,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default);
}

public sealed record OctopusImportTemporaryUploadCleanupCandidate(
    Guid SessionId,
    string TemporaryUploadPath,
    OctopusImportSessionState State,
    DateTimeOffset? CleanupAfter);

public class OctopusImportSessionDataProvider : IOctopusImportSessionDataProvider
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public OctopusImportSessionDataProvider(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task AddSessionAsync(OctopusImportSession session, bool forceSave = true, CancellationToken ct = default)
    {
        session.DataVersion ??= Guid.NewGuid().ToByteArray();
        session.LastStateChangedAt = session.LastStateChangedAt == default ? DateTimeOffset.UtcNow : session.LastStateChangedAt;
        session.SourceSummaryJson ??= "{}";

        await _repository.InsertAsync(session, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<OctopusImportSession> GetSessionAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default)
    {
        return _repository.Query<OctopusImportSession>(s =>
                s.SessionId == sessionId &&
                s.OwnerUserId == ownerUserId &&
                s.DestinationSpaceId == destinationSpaceId)
            .FirstOrDefaultAsync(ct);
    }

    public Task<OctopusImportSession> GetSessionNoTrackingAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default)
    {
        return _repository.QueryNoTracking<OctopusImportSession>(s =>
                s.SessionId == sessionId &&
                s.OwnerUserId == ownerUserId &&
                s.DestinationSpaceId == destinationSpaceId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task UpdateSessionAsync(
        OctopusImportSession session,
        byte[] expectedDataVersion,
        bool forceSave = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectedDataVersion);

        var now = DateTimeOffset.UtcNow;
        var dataVersion = Guid.NewGuid().ToByteArray();

        session.ProtectedExportPassword = RetainedExportPassword(session.State, session.ProtectedExportPassword);

        var rowsAffected = await _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.Id == session.Id &&
                 s.DataVersion == expectedDataVersion,
            setters => setters
                .SetProperty(s => s.SessionId, session.SessionId)
                .SetProperty(s => s.DestinationSpaceId, session.DestinationSpaceId)
                .SetProperty(s => s.OwnerUserId, session.OwnerUserId)
                .SetProperty(s => s.State, session.State)
                .SetProperty(s => s.SourceSummaryJson, session.SourceSummaryJson ?? "{}")
                .SetProperty(s => s.RedactedNormalizedDataJson, session.RedactedNormalizedDataJson)
                .SetProperty(s => s.ValidatedPlanJson, session.ValidatedPlanJson)
                .SetProperty(s => s.ResultJson, session.ResultJson)
                .SetProperty(s => s.TemporaryUploadPath, session.TemporaryUploadPath)
                .SetProperty(s => s.TemporaryUploadSizeBytes, session.TemporaryUploadSizeBytes)
                .SetProperty(s => s.TemporaryUploadCleanupAfter, session.TemporaryUploadCleanupAfter)
                .SetProperty(s => s.TemporaryUploadCleanedAt, session.TemporaryUploadCleanedAt)
                .SetProperty(s => s.TemporaryUploadCleanupError, session.TemporaryUploadCleanupError)
                .SetProperty(s => s.ProtectedExportPassword, session.ProtectedExportPassword)
                .SetProperty(s => s.ExpiresAt, session.ExpiresAt)
                .SetProperty(s => s.CompletedAt, session.CompletedAt)
                .SetProperty(s => s.LastStateChangedAt, session.LastStateChangedAt)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.DataVersion, dataVersion),
            ct).ConfigureAwait(false);

        _repository.Detach(session);

        if (rowsAffected != 1)
            throw new OctopusImportSessionConcurrencyException(session.SessionId);

        session.DataVersion = dataVersion;
        session.LastModifiedDate = now;
    }

    public Task<int> TransitionStateAsync(
        Guid sessionId,
        int ownerUserId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        CancellationToken ct = default)
        => TransitionStateAsync(
            sessionId,
            ownerUserId,
            destinationSpaceId,
            expectedState,
            newState,
            null,
            ct);

    public Task<int> TransitionStateAsync(
        Guid sessionId,
        int ownerUserId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        byte[] expectedDataVersion,
        CancellationToken ct = default)
        => TransitionStateAsync(sessionId, ownerUserId, destinationSpaceId, expectedState, newState, expectedDataVersion, null, ct);

    private Task<int> TransitionStateAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, OctopusImportSessionState expectedState, OctopusImportSessionState newState, byte[] expectedDataVersion, DateTimeOffset? createdAfter, CancellationToken ct)
    {
        OctopusImportSessionStateMachine.EnsureValidTransition(expectedState, newState);

        var now = DateTimeOffset.UtcNow;
        var dataVersion = Guid.NewGuid().ToByteArray();
        var isTerminal = OctopusImportSessionStateMachine.IsTerminal(newState);
        var completedAt = isTerminal ? now : (DateTimeOffset?)null;

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.SessionId == sessionId &&
                 s.OwnerUserId == ownerUserId &&
                 s.DestinationSpaceId == destinationSpaceId &&
                 s.State == expectedState.ToString() &&
                 (expectedDataVersion == null || s.DataVersion == expectedDataVersion) &&
                 (createdAfter == null || s.CreatedDate > createdAfter),
            setters => setters
                .SetProperty(s => s.State, newState.ToString())
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.CompletedAt, completedAt)
                .SetProperty(s => s.ProtectedExportPassword, s => isTerminal ? null : s.ProtectedExportPassword),
            ct);
    }

    // Single enforcement point for the "export password lives only while the session is active" invariant
    // on the generic write path: whatever a caller set, a session written in a terminal state never keeps it.
    private static string RetainedExportPassword(string state, string protectedExportPassword)
        => Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed) && OctopusImportSessionStateMachine.IsTerminal(parsed) ? null : protectedExportPassword;

    public async Task<bool> TryStartConfirmationAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, byte[] expectedDataVersion, DateTimeOffset createdAfter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expectedDataVersion);

        var rowsAffected = await TransitionStateAsync(sessionId, ownerUserId, destinationSpaceId, OctopusImportSessionState.Validated, OctopusImportSessionState.Importing, expectedDataVersion, createdAfter, ct).ConfigureAwait(false);

        return rowsAffected == 1;
    }

    public async Task<OctopusImportHistoryFetchDto> GetLatestHistoryFetchAsync(int destinationSpaceId, string installationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installationId)) return null;

        var json = await _repository.Database.SqlQuery<string>($"SELECT source_summary_json::text AS \"Value\" FROM octopus_import_session WHERE destination_space_id = {destinationSpaceId} AND upper(source_summary_json -> 'historyFetch' ->> 'installationId') = upper({installationId.Trim()}) ORDER BY id DESC LIMIT 1").ToListAsync(ct).ConfigureAwait(false);

        return json.Count == 0 ? null : OctopusImportSourceSummaries.Parse(json[0])?.HistoryFetch;
    }

    public Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var dataVersion = Guid.NewGuid().ToByteArray();

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.ExpiresAt <= now &&
                 s.State != OctopusImportSessionState.Succeeded.ToString() &&
                 s.State != OctopusImportSessionState.Failed.ToString() &&
                 s.State != OctopusImportSessionState.Expired.ToString() &&
                 s.State != OctopusImportSessionState.Importing.ToString(),
            setters => setters
                .SetProperty(s => s.State, OctopusImportSessionState.Expired.ToString())
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.CompletedAt, now)
                .SetProperty(s => s.TemporaryUploadCleanupAfter, now)
                .SetProperty(s => s.ProtectedExportPassword, (string)null),
            ct);
    }

    public Task<int> ExpireOverAgeSessionsAsync(DateTimeOffset now, DateTimeOffset createdBefore, string resultJson, CancellationToken ct = default)
    {
        var dataVersion = Guid.NewGuid().ToByteArray();

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.CreatedDate <= createdBefore &&
                 s.State != OctopusImportSessionState.Succeeded.ToString() &&
                 s.State != OctopusImportSessionState.Failed.ToString() &&
                 s.State != OctopusImportSessionState.Expired.ToString() &&
                 s.State != OctopusImportSessionState.Importing.ToString(),
            setters => setters
                .SetProperty(s => s.State, OctopusImportSessionState.Expired.ToString())
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.CompletedAt, now)
                .SetProperty(s => s.ExpiresAt, now)
                .SetProperty(s => s.ResultJson, resultJson)
                .SetProperty(s => s.TemporaryUploadCleanupAfter, now)
                .SetProperty(s => s.ProtectedExportPassword, (string)null),
            ct);
    }

    public Task<int> MarkInterruptedImportsFailedAsync(
        DateTimeOffset now,
        DateTimeOffset staleBefore,
        TimeSpan failedRetentionPeriod,
        CancellationToken ct = default)
    {
        var dataVersion = Guid.NewGuid().ToByteArray();
        var cleanupAfter = now.Add(failedRetentionPeriod);

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.LastStateChangedAt <= staleBefore &&
                 s.State == OctopusImportSessionState.Importing.ToString(),
            setters => setters
                .SetProperty(s => s.State, OctopusImportSessionState.Failed.ToString())
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.CompletedAt, now)
                .SetProperty(s => s.TemporaryUploadCleanupAfter, cleanupAfter)
                .SetProperty(s => s.ProtectedExportPassword, (string)null),
            ct);
    }

    public async Task<IReadOnlyList<OctopusImportTemporaryUploadCleanupCandidate>> GetTemporaryUploadCleanupCandidatesAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        var effectiveLimit = Math.Max(1, limit);

        var sessions = await _repository.QueryNoTracking<OctopusImportSession>(s =>
                s.TemporaryUploadPath != null &&
                s.TemporaryUploadPath != "" &&
                s.TemporaryUploadCleanedAt == null &&
                (s.TemporaryUploadCleanupAfter == null || s.TemporaryUploadCleanupAfter <= now) &&
                (s.State == OctopusImportSessionState.Succeeded.ToString() ||
                 s.State == OctopusImportSessionState.Failed.ToString() ||
                 s.State == OctopusImportSessionState.Expired.ToString()))
            .OrderBy(s => s.TemporaryUploadCleanupAfter ?? s.CompletedAt ?? s.ExpiresAt)
            .Take(effectiveLimit)
            .Select(s => new
            {
                s.SessionId,
                s.TemporaryUploadPath,
                s.State,
                s.TemporaryUploadCleanupAfter
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return sessions
            .Select(s => new OctopusImportTemporaryUploadCleanupCandidate(
                s.SessionId,
                s.TemporaryUploadPath,
                Enum.Parse<OctopusImportSessionState>(s.State),
                s.TemporaryUploadCleanupAfter))
            .ToList();
    }

    public Task<int> MarkTemporaryUploadCleanupFailedAsync(
        Guid sessionId,
        string temporaryUploadPath,
        string error,
        DateTimeOffset attemptedAt,
        CancellationToken ct = default)
    {
        var dataVersion = Guid.NewGuid().ToByteArray();
        var safeError = SanitizeTemporaryUploadCleanupError(error);

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.SessionId == sessionId &&
                 s.TemporaryUploadPath == temporaryUploadPath &&
                 s.TemporaryUploadCleanedAt == null,
            setters => setters
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastModifiedDate, attemptedAt)
                .SetProperty(s => s.TemporaryUploadCleanupError, safeError),
            ct);
    }

    internal static string SanitizeTemporaryUploadCleanupError(string error)
    {
        var safeError = string.IsNullOrWhiteSpace(error)
            ? "Temporary upload cleanup failed."
            : OctopusImportRedaction.RedactMetadataValue("TemporaryUploadCleanupError", error);

        return safeError.Length > 1024 ? safeError[..1024] : safeError;
    }
}
