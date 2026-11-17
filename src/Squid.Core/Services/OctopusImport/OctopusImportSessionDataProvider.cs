using System.Linq.Expressions;
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

    /// <summary>
    /// The same admission, recording <see cref="OctopusImportConfirmationAdmission.AttemptId"/> as the attempt that holds the session and
    /// starting its heartbeat: every later write that ends the attempt is fenced on that id.
    /// </summary>
    Task<bool> TryStartConfirmationAsync(OctopusImportConfirmationAdmission admission, CancellationToken ct = default);

    /// <summary>
    /// Refreshes only the heartbeat of the attempt that still holds the session (no data version, no modification date), stamped by the
    /// database clock: the recovery sweep measures its age on that same clock, so the servers' clocks never have to agree.
    /// </summary>
    Task<int> TouchConfirmationHeartbeatAsync(Guid sessionId, Guid attemptId, CancellationToken ct = default);

    /// <summary>
    /// Writes the live progress of <paramref name="attemptId"/>, on whatever connection the caller's scope holds. Fenced on the attempt only
    /// (not the state), so the attempt's final snapshot still lands after its own Succeeded or Failed settle; never changes the data version
    /// or the modification date (the settle's concurrency check and the resumable list's ordering depend on both).
    /// </summary>
    Task<int> WriteConfirmationProgressAsync(Guid sessionId, Guid attemptId, string progressJson, CancellationToken ct = default);

    /// <summary>Whether <paramref name="attemptId"/> still holds the session as Importing (read without tracking, outside any transaction it is not in).</summary>
    Task<bool> IsConfirmationHeldAsync(Guid sessionId, Guid attemptId, CancellationToken ct = default);

    /// <summary>
    /// Returns the session the attempt holds to Validated with <paramref name="failureJson"/>, keeping the export password (the session is
    /// not over); 0 when the attempt no longer holds it (it committed, or was recovered and another attempt started).
    /// </summary>
    Task<int> ReturnConfirmationToValidatedAsync(Guid sessionId, Guid attemptId, string failureJson, OctopusImportValidatedReturn validatedReturn, CancellationToken ct = default);

    /// <summary>Records why a Validated session's confirmation never started; nothing else changes (not even the data version).</summary>
    Task<int> RecordConfirmationFailureAsync(Guid sessionId, string failureJson, CancellationToken ct = default);

    /// <summary>Importing sessions whose heartbeat stopped before the cut-offs (or, started by an older server, that have none).</summary>
    Task<IReadOnlyList<OctopusImportInterruptedConfirmation>> GetInterruptedConfirmationsAsync(OctopusImportInterruptedConfirmationCutoffs cutoffs, int limit, CancellationToken ct = default);

    /// <summary>Returns one interrupted session to Validated, only while it is still the same attempt and still stale (a late beat wins).</summary>
    Task<int> RecoverInterruptedConfirmationAsync(OctopusImportInterruptedConfirmation interrupted, OctopusImportInterruptedConfirmationCutoffs cutoffs, string failureJson, OctopusImportValidatedReturn validatedReturn, CancellationToken ct = default);

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

    /// <summary>
    /// The owner's sessions in the space that can still be continued, newest first: not expired, not closed, upload not cleaned up, or
    /// Importing (followed until it ends, whatever its expiry). Only the small columns are read, never the source data or the plan.
    /// </summary>
    Task<IReadOnlyList<OctopusImportResumableSessionRow>> GetResumableSessionsNoTrackingAsync(int ownerUserId, int destinationSpaceId, DateTimeOffset now, int limit, CancellationToken ct = default);

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

public sealed record OctopusImportConfirmationAdmission(Guid SessionId, int OwnerUserId, int DestinationSpaceId, byte[] ExpectedDataVersion, DateTimeOffset CreatedAfter, Guid AttemptId);

/// <summary>An Importing session whose heartbeat stopped; <see cref="AttemptId"/> is null for one started by an older server.</summary>
public sealed record OctopusImportInterruptedConfirmation(Guid SessionId, Guid? AttemptId);

/// <param name="HeartbeatStale">A heartbeat at least this old, by the database clock that stamped it, is stale.</param>
/// <param name="LegacyStaleBefore">A session with no heartbeat (started by an older server) is stale once its state changed at or before this.</param>
public sealed record OctopusImportInterruptedConfirmationCutoffs(TimeSpan HeartbeatStale, DateTimeOffset LegacyStaleBefore);

/// <summary>When a session returns to Validated and how far its idle expiry slides: <see cref="Retention"/> ahead, never past <see cref="MaxAge"/> from creation.</summary>
public sealed record OctopusImportValidatedReturn(DateTimeOffset Now, TimeSpan Retention, TimeSpan MaxAge);

/// <summary>The columns of a session the resumable-session list reads: never the source data, the plan, the result or anything secret.</summary>
public sealed record OctopusImportResumableSessionRow(Guid SessionId, string State, string SourceSummaryJson, bool HasUpload, bool HasConfirmationFailure, DateTimeOffset CreatedDate, DateTimeOffset LastModifiedDate, DateTimeOffset LastStateChangedAt, DateTimeOffset ExpiresAt);

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
                .SetProperty(s => s.LastConfirmationFailureJson, session.LastConfirmationFailureJson)
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
                 (expectedDataVersion == null || s.DataVersion == expectedDataVersion),
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

    public Task<bool> TryStartConfirmationAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, byte[] expectedDataVersion, DateTimeOffset createdAfter, CancellationToken ct = default)
        => TryStartConfirmationAsync(new OctopusImportConfirmationAdmission(sessionId, ownerUserId, destinationSpaceId, expectedDataVersion, createdAfter, Guid.NewGuid()), ct);

    public async Task<bool> TryStartConfirmationAsync(OctopusImportConfirmationAdmission admission, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(admission.ExpectedDataVersion);

        var now = DateTimeOffset.UtcNow;
        var dataVersion = Guid.NewGuid().ToByteArray();
        var importing = OctopusImportSessionState.Importing.ToString();

        var rowsAffected = await _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.SessionId == admission.SessionId &&
                 s.OwnerUserId == admission.OwnerUserId &&
                 s.DestinationSpaceId == admission.DestinationSpaceId &&
                 s.State == OctopusImportSessionState.Validated.ToString() &&
                 s.DataVersion == admission.ExpectedDataVersion &&
                 s.CreatedDate > admission.CreatedAfter,
            setters => setters
                .SetProperty(s => s.State, importing)
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.CompletedAt, (DateTimeOffset?)null)
                .SetProperty(s => s.ConfirmationAttemptId, admission.AttemptId)
                .SetProperty(s => s.LastConfirmationFailureJson, (string)null)
                .SetProperty(s => s.ConfirmationProgressJson, (string)null)
                .SetProperty(s => s.ImportHeartbeatAt, s => DateTimeOffset.UtcNow),
            ct).ConfigureAwait(false);

        return rowsAffected == 1;
    }

    // DateTimeOffset.UtcNow inside the query is translated to the database's now(), never sent as this server's time.
    public Task<int> TouchConfirmationHeartbeatAsync(Guid sessionId, Guid attemptId, CancellationToken ct = default)
        => _repository.ExecuteUpdateAsync<OctopusImportSession>(HeldBy(sessionId, attemptId), setters => setters.SetProperty(s => s.ImportHeartbeatAt, s => DateTimeOffset.UtcNow), ct);

    public Task<int> WriteConfirmationProgressAsync(Guid sessionId, Guid attemptId, string progressJson, CancellationToken ct = default)
        => _repository.ExecuteUpdateAsync<OctopusImportSession>(s => s.SessionId == sessionId && s.ConfirmationAttemptId == attemptId, setters => setters.SetProperty(s => s.ConfirmationProgressJson, progressJson), ct);

    public Task<bool> IsConfirmationHeldAsync(Guid sessionId, Guid attemptId, CancellationToken ct = default)
        => _repository.QueryNoTracking(HeldBy(sessionId, attemptId)).AnyAsync(ct);

    public Task<int> ReturnConfirmationToValidatedAsync(Guid sessionId, Guid attemptId, string failureJson, OctopusImportValidatedReturn validatedReturn, CancellationToken ct = default)
        => ReturnToValidatedAsync(HeldBy(sessionId, attemptId), failureJson, validatedReturn, ct);

    public Task<int> RecordConfirmationFailureAsync(Guid sessionId, string failureJson, CancellationToken ct = default)
    {
        var validated = OctopusImportSessionState.Validated.ToString();

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(s => s.SessionId == sessionId && s.State == validated, setters => setters.SetProperty(s => s.LastConfirmationFailureJson, failureJson), ct);
    }

    public async Task<IReadOnlyList<OctopusImportInterruptedConfirmation>> GetInterruptedConfirmationsAsync(OctopusImportInterruptedConfirmationCutoffs cutoffs, int limit, CancellationToken ct = default)
    {
        var rows = await _repository.QueryNoTracking(Interrupted(cutoffs)).OrderBy(s => s.Id).Take(Math.Max(1, limit)).Select(s => new { s.SessionId, s.ConfirmationAttemptId }).ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new OctopusImportInterruptedConfirmation(r.SessionId, r.ConfirmationAttemptId)).ToList();
    }

    public Task<int> RecoverInterruptedConfirmationAsync(OctopusImportInterruptedConfirmation interrupted, OctopusImportInterruptedConfirmationCutoffs cutoffs, string failureJson, OctopusImportValidatedReturn validatedReturn, CancellationToken ct = default)
    {
        var stale = Interrupted(cutoffs);
        var attemptId = interrupted.AttemptId;

        return ReturnToValidatedAsync(And(stale, s => s.SessionId == interrupted.SessionId && s.ConfirmationAttemptId == attemptId), failureJson, validatedReturn, ct);
    }

    // The attempt still holds the session: Importing, under that attempt's fencing token.
    private static Expression<Func<OctopusImportSession, bool>> HeldBy(Guid sessionId, Guid attemptId)
    {
        var importing = OctopusImportSessionState.Importing.ToString();

        return s => s.SessionId == sessionId && s.State == importing && s.ConfirmationAttemptId == attemptId;
    }

    private static Expression<Func<OctopusImportSession, bool>> Interrupted(OctopusImportInterruptedConfirmationCutoffs cutoffs)
    {
        var importing = OctopusImportSessionState.Importing.ToString();
        var (heartbeatStale, legacyStaleBefore) = (cutoffs.HeartbeatStale, cutoffs.LegacyStaleBefore);

        // The heartbeat's age on the database clock that stamped it (now() in the query), so a sweeping server whose clock runs ahead of
        // the importing server's never mistakes a live import for a dead one.
        return s => s.State == importing && ((s.ImportHeartbeatAt != null && s.ImportHeartbeatAt <= DateTimeOffset.UtcNow - heartbeatStale) || (s.ImportHeartbeatAt == null && s.LastStateChangedAt <= legacyStaleBefore));
    }

    // Validated again under a new data version (a plan read before is not imported in its place), the password kept, the attempt released.
    // An import can outlast the idle expiry, so the session (and its upload) is kept a retention period from now, for "Import again" —
    // never past its maximum age (the over-age sweep still closes it on time), never earlier than it was. Per row, in the database, as
    // SlideExpiryAhead does for one session: GREATEST(expires_at, LEAST(now + retention, created_date + max age)).
    private Task<int> ReturnToValidatedAsync(Expression<Func<OctopusImportSession, bool>> predicate, string failureJson, OctopusImportValidatedReturn validatedReturn, CancellationToken ct)
    {
        var dataVersion = Guid.NewGuid().ToByteArray();
        var validated = OctopusImportSessionState.Validated.ToString();
        var (now, slid, maxAge) = (validatedReturn.Now, validatedReturn.Now.Add(validatedReturn.Retention), validatedReturn.MaxAge);

        return _repository.ExecuteUpdateAsync(
            predicate,
            setters => setters
                .SetProperty(s => s.State, validated)
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.LastConfirmationFailureJson, failureJson)
                .SetProperty(s => s.ConfirmationAttemptId, (Guid?)null)
                .SetProperty(s => s.ImportHeartbeatAt, (DateTimeOffset?)null)
                .SetProperty(s => s.ExpiresAt, s => s.ExpiresAt >= (slid <= s.CreatedDate + maxAge ? slid : s.CreatedDate + maxAge) ? s.ExpiresAt : (slid <= s.CreatedDate + maxAge ? slid : s.CreatedDate + maxAge))
                .SetProperty(s => s.TemporaryUploadCleanupAfter, s => s.ExpiresAt >= (slid <= s.CreatedDate + maxAge ? slid : s.CreatedDate + maxAge) ? s.TemporaryUploadCleanupAfter : (slid <= s.CreatedDate + maxAge ? slid : s.CreatedDate + maxAge)),
            ct);
    }

    private static Expression<Func<T, bool>> And<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var body = Expression.AndAlso(left.Body, new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body)!);

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
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

    public async Task<IReadOnlyList<OctopusImportResumableSessionRow>> GetResumableSessionsNoTrackingAsync(int ownerUserId, int destinationSpaceId, DateTimeOffset now, int limit, CancellationToken ct = default)
    {
        var rows = await _repository.QueryNoTracking(Resumable(ownerUserId, destinationSpaceId, now))
            .OrderByDescending(s => s.LastModifiedDate)
            .Take(Math.Max(1, limit))
            .Select(s => new { s.SessionId, s.State, s.SourceSummaryJson, HasUpload = s.TemporaryUploadPath != null, HasConfirmationFailure = s.LastConfirmationFailureJson != null, s.CreatedDate, s.LastModifiedDate, s.LastStateChangedAt, s.ExpiresAt })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new OctopusImportResumableSessionRow(r.SessionId, r.State, r.SourceSummaryJson, r.HasUpload, r.HasConfirmationFailure, r.CreatedDate, r.LastModifiedDate, r.LastStateChangedAt, r.ExpiresAt)).ToList();
    }

    // An open session is resumable while it lasts; a running import is followed until it ends, whatever its expiry (the sweep never expires it).
    // An export file whose upload was never registered has nothing to continue (OctopusImportResumeSteps): it is left out here, before
    // the row cap, so such orphans never crowd out older sessions that can be continued.
    private static Expression<Func<OctopusImportSession, bool>> Resumable(int ownerUserId, int destinationSpaceId, DateTimeOffset now)
    {
        var open = new[] { OctopusImportSessionState.Uploaded, OctopusImportSessionState.Extracted, OctopusImportSessionState.Previewed, OctopusImportSessionState.Validated }.Select(state => state.ToString()).ToList();
        var importing = OctopusImportSessionState.Importing.ToString();
        var uploaded = OctopusImportSessionState.Uploaded.ToString();

        return s => s.OwnerUserId == ownerUserId && s.DestinationSpaceId == destinationSpaceId && ((open.Contains(s.State) && s.ExpiresAt > now && s.TemporaryUploadCleanedAt == null && !(s.State == uploaded && s.TemporaryUploadPath == null && !EF.Functions.JsonContains(s.SourceSummaryJson, ServerSourceSummaryMarker))) || s.State == importing);
    }

    // The jsonb fragment every read from Octopus carries in its source summary (camelCase, as the session service writes it).
    private static readonly string ServerSourceSummaryMarker = $$"""{"detectedFormat":"{{OctopusImportServerSourceService.ServerSourceFormat}}"}""";

    public async Task<IReadOnlyList<OctopusImportTemporaryUploadCleanupCandidate>> GetTemporaryUploadCleanupCandidatesAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        var effectiveLimit = Math.Max(1, limit);

        // A closed session with no registered upload is a candidate too: its upload may have been stored before the request registering it failed.
        var sessions = await _repository.QueryNoTracking<OctopusImportSession>(s =>
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
