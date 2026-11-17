using System.Text.Json;
using System.Text.Json.Serialization;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportSessionService : IScopedDependency
{
    Task<OctopusImportSessionDto> CreateSessionAsync(
        int destinationSpaceId,
        OctopusImportSourceSummaryDto sourceSummary,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    Task<OctopusImportSessionDto> CreateSessionAsync(
        int destinationSpaceId,
        OctopusImportSourceSummaryDto sourceSummary,
        CancellationToken ct = default);

    Task<OctopusImportSessionDto> GetSessionAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default);

    Task<OctopusImportSessionDto> RegisterTemporaryUploadAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportTemporaryUpload temporaryUpload,
        CancellationToken ct = default);

    Task<OctopusImportSessionDto> RegisterTemporaryUploadAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportTemporaryUpload temporaryUpload,
        OctopusImportSourceSummaryDto sourceSummary,
        CancellationToken ct = default);

    Task<OctopusImportSessionDto> UpdatePayloadAndTransitionAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        string redactedNormalizedDataJson = null,
        string validatedPlanJson = null,
        CancellationToken ct = default);

    /// <summary>
    /// Replaces (or with null clears) the validated plan of a session that stays Validated: a validation after a changed selection must be
    /// what confirmation imports. Any other state throws <see cref="OctopusImportSessionStateTransitionException"/>.
    /// </summary>
    Task<OctopusImportSessionDto> ReplaceValidatedPlanAsync(Guid sessionId, int destinationSpaceId, string validatedPlanJson, CancellationToken ct = default);

    /// <summary>
    /// Moves a Validated session to Importing, atomically. With <paramref name="expectedDataVersion"/> (the version the validated plan was
    /// read with) it is admitted only while the session still holds that version; without it, the current version is expected.
    /// </summary>
    Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, byte[] expectedDataVersion = null, CancellationToken ct = default);

    /// <summary>The same admission under the caller's <paramref name="attemptId"/>, which fences every write that ends the attempt.</summary>
    Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, Guid attemptId, byte[] expectedDataVersion, CancellationToken ct = default);

    /// <summary>Whether the attempt still holds the session as Importing; false once it committed or was recovered.</summary>
    Task<bool> IsConfirmationHeldAsync(Guid sessionId, Guid attemptId, CancellationToken ct = default);

    /// <summary>
    /// Ends the attempt's session in <paramref name="terminalState"/> with <paramref name="result"/>, like <see cref="RecordResultAsync"/>,
    /// only while <paramref name="attemptId"/> still holds it; otherwise throws <see cref="OctopusImportConfirmationFencedException"/>.
    /// </summary>
    Task<OctopusImportSessionDto> SettleConfirmationAsync(Guid sessionId, int destinationSpaceId, Guid attemptId, OctopusImportSessionState terminalState, OctopusImportSessionResultDto result, CancellationToken ct = default);

    /// <summary>
    /// Returns the session held by <paramref name="failure"/>'s attempt to Validated, recording the failure; the session as it is when the
    /// attempt no longer holds it (its commit landed after all, or it was recovered).
    /// </summary>
    Task<OctopusImportSessionDto> ReturnConfirmationToValidatedAsync(Guid sessionId, int destinationSpaceId, OctopusImportConfirmationFailureDto failure, CancellationToken ct = default);

    /// <summary>Records on a Validated session why its confirmation never started; the session stays Validated, importable as it is.</summary>
    Task<OctopusImportSessionDto> RecordConfirmationNotStartedAsync(Guid sessionId, int destinationSpaceId, OctopusImportConfirmationFailureDto failure, CancellationToken ct = default);

    /// <summary>
    /// Replaces the REST history fetch state of an Uploaded session. <paramref name="expectedChunkId"/> must match the
    /// stored chunk id and the row's data version must be unchanged, otherwise a concurrent chunk won and this throws
    /// <see cref="OctopusImportSessionConcurrencyException"/>.
    /// </summary>
    Task<OctopusImportSessionDto> UpdateHistoryFetchAsync(Guid sessionId, int destinationSpaceId, OctopusImportHistoryFetchDto historyFetch, Guid? expectedChunkId, CancellationToken ct = default);

    /// <summary>
    /// Stores the session's project selection (null = whole space), admitted only while the session still holds
    /// <paramref name="expectedDataVersion"/> (otherwise <see cref="OctopusImportSessionConcurrencyException"/>). A Validated session
    /// loses its validated plan with it: confirmation refuses until a validation with the new selection passes.
    /// </summary>
    Task<OctopusImportSessionDto> UpdateProjectSelectionAsync(Guid sessionId, int destinationSpaceId, List<string> projectIds, byte[] expectedDataVersion, CancellationToken ct = default);

    Task<OctopusImportSessionDto> RecordResultAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState terminalState,
        OctopusImportSessionResultDto result,
        CancellationToken ct = default);

    Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>The current user's sessions in the space that can still be continued (newest first, at most <see cref="OctopusImportSessionService.MaxListedSessions"/>); read-only.</summary>
    Task<IReadOnlyList<OctopusImportResumableSessionDto>> ListResumableSessionsAsync(int destinationSpaceId, CancellationToken ct = default);
}

public partial class OctopusImportSessionService : IOctopusImportSessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOctopusImportSessionDataProvider _dataProvider;
    private readonly ICurrentUser _currentUser;
    private readonly IOctopusImportTemporaryUploadSettings _temporaryUploadSettings;

    public OctopusImportSessionService(
        IOctopusImportSessionDataProvider dataProvider,
        ICurrentUser currentUser,
        IOctopusImportTemporaryUploadSettings temporaryUploadSettings)
    {
        _dataProvider = dataProvider;
        _currentUser = currentUser;
        _temporaryUploadSettings = temporaryUploadSettings;
    }

    public async Task<OctopusImportSessionDto> CreateSessionAsync(
        int destinationSpaceId,
        OctopusImportSourceSummaryDto sourceSummary,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var ownerUserId = GetCurrentUserId();
        var now = DateTimeOffset.UtcNow;
        var effectiveExpiresAt = GetEffectiveExpiresAt(expiresAt, now);

        var session = new OctopusImportSession
        {
            SessionId = Guid.NewGuid(),
            DestinationSpaceId = destinationSpaceId,
            OwnerUserId = ownerUserId,
            State = OctopusImportSessionState.Uploaded.ToString(),
            SourceSummaryJson = Serialize(OctopusImportRedaction.RedactDto(sourceSummary ?? new OctopusImportSourceSummaryDto())),
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = effectiveExpiresAt,
            TemporaryUploadCleanupAfter = effectiveExpiresAt,
            LastStateChangedAt = now,
            CreatedDate = now
        };

        await _dataProvider.AddSessionAsync(session, ct: ct).ConfigureAwait(false);

        return Map(session);
    }

    public Task<OctopusImportSessionDto> CreateSessionAsync(
        int destinationSpaceId,
        OctopusImportSourceSummaryDto sourceSummary,
        CancellationToken ct = default)
    {
        return CreateSessionAsync(
            destinationSpaceId,
            sourceSummary,
            DateTimeOffset.UtcNow.Add(_temporaryUploadSettings.DefaultRetentionPeriod),
            ct);
    }

    public async Task<OctopusImportSessionDto> GetSessionAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
    {
        var session = await _dataProvider
            .GetSessionNoTrackingAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct)
            .ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(sessionId);

        return Map(session);
    }

    public async Task<OctopusImportSessionDto> RegisterTemporaryUploadAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportTemporaryUpload temporaryUpload,
        CancellationToken ct = default)
    {
        return await RegisterTemporaryUploadAsync(sessionId, destinationSpaceId, temporaryUpload, null, ct).ConfigureAwait(false);
    }

    public async Task<OctopusImportSessionDto> RegisterTemporaryUploadAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportTemporaryUpload temporaryUpload,
        OctopusImportSourceSummaryDto sourceSummary,
        CancellationToken ct = default)
    {
        if (temporaryUpload == null)
            throw new ArgumentNullException(nameof(temporaryUpload));
        if (string.IsNullOrWhiteSpace(temporaryUpload.Path))
            throw new ArgumentException("Temporary upload path is required.", nameof(temporaryUpload));

        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
        var currentState = ParseState(session.State);
        if (currentState != OctopusImportSessionState.Uploaded)
            throw new OctopusImportSessionStateTransitionException(currentState, OctopusImportSessionState.Uploaded);

        EnsureWithinMaxAge(session);

        session.TemporaryUploadPath = temporaryUpload.Path;
        session.TemporaryUploadSizeBytes = temporaryUpload.SizeBytes;
        session.TemporaryUploadCleanupAfter = session.ExpiresAt;
        session.TemporaryUploadCleanedAt = null;
        session.TemporaryUploadCleanupError = null;
        if (sourceSummary != null)
            session.SourceSummaryJson = Serialize(OctopusImportRedaction.RedactDto(KeepHistoryFetch(sourceSummary, session.SourceSummaryJson)));

        await _dataProvider
            .UpdateSessionAsync(session, session.DataVersion, ct: ct)
            .ConfigureAwait(false);

        return Map(session);
    }

    public async Task<OctopusImportSessionDto> UpdatePayloadAndTransitionAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        string redactedNormalizedDataJson = null,
        string validatedPlanJson = null,
        CancellationToken ct = default)
    {
        OctopusImportSessionStateMachine.EnsureValidTransition(expectedState, newState);

        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
        var currentState = ParseState(session.State);
        OctopusImportSessionStateMachine.EnsureValidTransition(currentState, newState);

        if (currentState != expectedState)
            throw new OctopusImportSessionStateTransitionException(currentState, newState);

        if (!OctopusImportSessionStateMachine.IsTerminal(newState)) EnsureWithinMaxAge(session);

        session.State = newState.ToString();
        session.LastStateChangedAt = DateTimeOffset.UtcNow;
        session.RedactedNormalizedDataJson = redactedNormalizedDataJson == null
            ? session.RedactedNormalizedDataJson
            : OctopusImportRedaction.RedactJson(redactedNormalizedDataJson);
        session.ValidatedPlanJson = validatedPlanJson == null
            ? session.ValidatedPlanJson
            : OctopusImportRedaction.RedactJson(validatedPlanJson);

        if (OctopusImportSessionStateMachine.IsTerminal(newState))
        {
            session.ProtectedExportPassword = null;
            session.CompletedAt = session.LastStateChangedAt;
            session.TemporaryUploadCleanupAfter = GetTerminalUploadCleanupAfter(newState, session.LastStateChangedAt);
        }

        await _dataProvider
            .UpdateSessionAsync(session, session.DataVersion, ct: ct)
            .ConfigureAwait(false);

        return Map(session);
    }

    public async Task<OctopusImportSessionDto> ReplaceValidatedPlanAsync(Guid sessionId, int destinationSpaceId, string validatedPlanJson, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
        var currentState = ParseState(session.State);

        if (currentState != OctopusImportSessionState.Validated)
            throw new OctopusImportSessionStateTransitionException(currentState, OctopusImportSessionState.Validated);

        EnsureWithinMaxAge(session);

        session.ValidatedPlanJson = validatedPlanJson == null ? null : OctopusImportRedaction.RedactJson(validatedPlanJson);
        ForgetConfirmationFailure(session);

        await _dataProvider.UpdateSessionAsync(session, session.DataVersion, ct: ct).ConfigureAwait(false);

        return Map(session);
    }

    public Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, byte[] expectedDataVersion = null, CancellationToken ct = default)
        => TryStartConfirmationAsync(sessionId, destinationSpaceId, Guid.NewGuid(), expectedDataVersion, ct);

    public async Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, Guid attemptId, byte[] expectedDataVersion, CancellationToken ct = default)
    {
        var ownerUserId = GetCurrentUserId();
        var session = await _dataProvider.GetSessionNoTrackingAsync(sessionId, ownerUserId, destinationSpaceId, ct).ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(sessionId);

        EnsureWithinMaxAge(session);

        var createdAfter = DateTimeOffset.UtcNow.Subtract(_temporaryUploadSettings.MaxSessionAge);

        return await _dataProvider.TryStartConfirmationAsync(new OctopusImportConfirmationAdmission(sessionId, ownerUserId, destinationSpaceId, expectedDataVersion ?? session.DataVersion, createdAfter, attemptId), ct).ConfigureAwait(false);
    }

    public Task<bool> IsConfirmationHeldAsync(Guid sessionId, Guid attemptId, CancellationToken ct = default) => _dataProvider.IsConfirmationHeldAsync(sessionId, attemptId, ct);

    public async Task<OctopusImportSessionDto> SettleConfirmationAsync(Guid sessionId, int destinationSpaceId, Guid attemptId, OctopusImportSessionState terminalState, OctopusImportSessionResultDto result, CancellationToken ct = default)
    {
        EnsureTerminal(terminalState);

        // Read from the database, never the change tracker: a session tracked earlier in this scope would show a state the fence must not trust.
        var session = await _dataProvider.GetSessionNoTrackingAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct).ConfigureAwait(false) ?? throw new OctopusImportSessionNotFoundException(sessionId);

        if (ParseState(session.State) != OctopusImportSessionState.Importing || session.ConfirmationAttemptId != attemptId)
            throw new OctopusImportConfirmationFencedException(sessionId, attemptId);

        return await RecordAsync(session, terminalState, result, ct).ConfigureAwait(false);
    }

    public async Task<OctopusImportSessionDto> ReturnConfirmationToValidatedAsync(Guid sessionId, int destinationSpaceId, OctopusImportConfirmationFailureDto failure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failure);

        await _dataProvider.ReturnConfirmationToValidatedAsync(sessionId, failure.AttemptId, SerializeFailure(failure), ValidatedReturn(DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        return await GetSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
    }

    public async Task<OctopusImportSessionDto> RecordConfirmationNotStartedAsync(Guid sessionId, int destinationSpaceId, OctopusImportConfirmationFailureDto failure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var session = await GetSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);

        await _dataProvider.RecordConfirmationFailureAsync(session.SessionId, SerializeFailure(failure), ct).ConfigureAwait(false);

        return await GetSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
    }

    public async Task<OctopusImportSessionDto> UpdateHistoryFetchAsync(Guid sessionId, int destinationSpaceId, OctopusImportHistoryFetchDto historyFetch, Guid? expectedChunkId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(historyFetch);

        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
        var currentState = ParseState(session.State);

        if (currentState != OctopusImportSessionState.Uploaded)
            throw new OctopusImportSessionStateTransitionException(currentState, OctopusImportSessionState.Uploaded);

        EnsureWithinMaxAge(session);
        EnsureLeaseWithinMaxAge(session, historyFetch);

        var summary = Deserialize<OctopusImportSourceSummaryDto>(session.SourceSummaryJson) ?? new OctopusImportSourceSummaryDto();

        if (summary.HistoryFetch?.ChunkId != expectedChunkId)
            throw new OctopusImportSessionConcurrencyException(sessionId);

        summary.HistoryFetch = historyFetch;
        session.SourceSummaryJson = Serialize(OctopusImportRedaction.RedactDto(summary));

        SlideExpiryAhead(session);

        await _dataProvider.UpdateSessionAsync(session, session.DataVersion, ct: ct).ConfigureAwait(false);

        return Map(session);
    }

    public async Task<OctopusImportSessionDto> UpdateProjectSelectionAsync(Guid sessionId, int destinationSpaceId, List<string> projectIds, byte[] expectedDataVersion, CancellationToken ct = default)
    {
        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);

        EnsureWithinMaxAge(session);

        var summary = Deserialize<OctopusImportSourceSummaryDto>(session.SourceSummaryJson) ?? new OctopusImportSourceSummaryDto();

        summary.SelectedProjectIds = projectIds;
        session.SourceSummaryJson = Serialize(OctopusImportRedaction.RedactDto(summary));

        if (ParseState(session.State) == OctopusImportSessionState.Validated) session.ValidatedPlanJson = null;

        ForgetConfirmationFailure(session);

        await _dataProvider.UpdateSessionAsync(session, expectedDataVersion, ct: ct).ConfigureAwait(false);

        return Map(session);
    }

    public async Task<OctopusImportSessionDto> RecordResultAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState terminalState,
        OctopusImportSessionResultDto result,
        CancellationToken ct = default)
    {
        EnsureTerminal(terminalState);

        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);

        return await RecordAsync(session, terminalState, result, ct).ConfigureAwait(false);
    }

    private static void EnsureTerminal(OctopusImportSessionState terminalState)
    {
        if (!OctopusImportSessionStateMachine.IsTerminal(terminalState))
            throw new ArgumentException($"State '{terminalState}' is not terminal.", nameof(terminalState));
    }

    private async Task<OctopusImportSessionDto> RecordAsync(OctopusImportSession session, OctopusImportSessionState terminalState, OctopusImportSessionResultDto result, CancellationToken ct)
    {
        var currentState = ParseState(session.State);

        if (currentState != terminalState)
            OctopusImportSessionStateMachine.EnsureValidTransition(currentState, terminalState);

        var completedAt = DateTimeOffset.UtcNow;
        result ??= new OctopusImportSessionResultDto();
        result.Succeeded = terminalState == OctopusImportSessionState.Succeeded;
        result.CompletedAt = completedAt;

        session.State = terminalState.ToString();
        session.ResultJson = Serialize(OctopusImportRedaction.RedactDto(result));
        session.CompletedAt = completedAt;
        session.LastStateChangedAt = completedAt;
        session.TemporaryUploadCleanupAfter = GetTerminalUploadCleanupAfter(terminalState, completedAt);
        session.ProtectedExportPassword = null;

        await _dataProvider
            .UpdateSessionAsync(session, session.DataVersion, ct: ct)
            .ConfigureAwait(false);

        return Map(session);
    }

    public Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        return _dataProvider.ExpireSessionsAsync(now, ct);
    }

    private async Task<OctopusImportSession> GetOwnedSessionAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct)
    {
        var session = await _dataProvider.GetSessionAsync(sessionId, GetCurrentUserId(), destinationSpaceId, ct).ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(sessionId);

        return session;
    }

    // A summary written by upload knows nothing about a history fetch already recorded on the session; keep it.
    private static OctopusImportSourceSummaryDto KeepHistoryFetch(OctopusImportSourceSummaryDto incoming, string storedJson)
    {
        incoming.HistoryFetch ??= Deserialize<OctopusImportSourceSummaryDto>(storedJson)?.HistoryFetch;

        return incoming;
    }

    private int GetCurrentUserId()
    {
        if (_currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import sessions require an authenticated user.");

        return _currentUser.Id.Value;
    }

    private OctopusImportSessionDto Map(OctopusImportSession session)
    {
        if (session == null)
            return null;

        return new OctopusImportSessionDto
        {
            SessionId = session.SessionId,
            DestinationSpaceId = session.DestinationSpaceId,
            OwnerUserId = session.OwnerUserId,
            State = ParseState(session.State),
            SourceSummary = WithoutKeyBinding(OctopusImportRedaction.RedactDto(Deserialize<OctopusImportSourceSummaryDto>(session.SourceSummaryJson))),
            Result = OctopusImportRedaction.RedactDto(Deserialize<OctopusImportSessionResultDto>(session.ResultJson)),
            ExpiresAt = session.ExpiresAt,
            MaxExpiresAt = OctopusImportSessionExpiry.Deadline(session.CreatedDate, _temporaryUploadSettings.MaxSessionAge),
            CompletedAt = session.CompletedAt,
            LastStateChangedAt = session.LastStateChangedAt,
            LastConfirmationFailure = RetryableFailure(session),
            ConfirmationProgress = VisibleProgress(session)
        };
    }

    // Only the progress of the attempt the session's state is about; redacted like the result it describes.
    private static OctopusImportConfirmationProgressDto VisibleProgress(OctopusImportSession session)
    {
        var progress = Deserialize<OctopusImportConfirmationProgressDto>(session.ConfirmationProgressJson);

        if (progress == null) return null;

        var state = ParseState(session.State);
        var failureAttemptId = Deserialize<OctopusImportConfirmationFailureDto>(session.LastConfirmationFailureJson)?.AttemptId;

        if (!OctopusImportConfirmationProgresses.Describes(state, session.ConfirmationAttemptId, failureAttemptId, progress.AttemptId)) return null;

        return OctopusImportRedaction.RedactDto(OctopusImportConfirmationProgresses.Settled(progress, state));
    }

    // A replaced plan or selection was never tried: the failure of an attempt at the plan before it no longer describes the session.
    private static void ForgetConfirmationFailure(OctopusImportSession session) => session.LastConfirmationFailureJson = null;

    // Only a Validated session can be imported again; once the session moves on, an older attempt's failure no longer describes it.
    private static OctopusImportConfirmationFailureDto RetryableFailure(OctopusImportSession session)
        => ParseState(session.State) == OctopusImportSessionState.Validated ? OctopusImportRedaction.RedactDto(Deserialize<OctopusImportConfirmationFailureDto>(session.LastConfirmationFailureJson)) : null;

    // The key fingerprint only binds chunks server-side; no response needs it.
    private static OctopusImportSourceSummaryDto WithoutKeyBinding(OctopusImportSourceSummaryDto summary)
    {
        if (summary?.HistoryFetch != null) summary.HistoryFetch.KeyHmac = null;

        return summary;
    }

    private static OctopusImportSessionState ParseState(string state)
    {
        return Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    /// <summary>A session result in the shape <see cref="RecordResultAsync"/> stores, for writers that bypass it (the over-age sweep).</summary>
    public static string SerializeResult(OctopusImportSessionResultDto result) => Serialize(OctopusImportRedaction.RedactDto(result));

    /// <summary>A confirmation's live progress in the shape the session stores it, for its writer on a connection of its own.</summary>
    public static string SerializeProgress(OctopusImportConfirmationProgressDto progress) => Serialize(OctopusImportRedaction.RedactDto(progress));

    /// <summary>A confirmation failure in the shape the session stores it, redacted, for writers that bypass this service (the recovery sweep).</summary>
    public static string SerializeFailure(OctopusImportConfirmationFailureDto failure) => Serialize(OctopusImportRedaction.RedactDto(failure));

    private DateTimeOffset GetTerminalUploadCleanupAfter(OctopusImportSessionState terminalState, DateTimeOffset completedAt)
    {
        return terminalState switch
        {
            OctopusImportSessionState.Succeeded => completedAt,
            OctopusImportSessionState.Expired => completedAt,
            OctopusImportSessionState.Failed => completedAt.Add(_temporaryUploadSettings.FailedRetentionPeriod),
            _ => completedAt
        };
    }

    // The return to Validated keeps the session a retention period from now, within its maximum age (the same rule as SlideExpiryAhead).
    internal OctopusImportValidatedReturn ValidatedReturn(DateTimeOffset now) => new(now, _temporaryUploadSettings.DefaultRetentionPeriod, _temporaryUploadSettings.MaxSessionAge);

    // A fetch in progress is in use: each chunk claim and release keeps the session (and its upload) a full retention
    // period away from expiry, so the expiry sweep never deletes a directory a running chunk is writing to. Never past the
    // session's maximum age, though: activity cannot keep a session alive forever.
    private void SlideExpiryAhead(OctopusImportSession session)
    {
        var slid = OctopusImportSessionExpiry.Clamp(DateTimeOffset.UtcNow.Add(_temporaryUploadSettings.DefaultRetentionPeriod), session.CreatedDate, _temporaryUploadSettings.MaxSessionAge);

        if (slid <= session.ExpiresAt) return;

        session.ExpiresAt = slid;
        session.TemporaryUploadCleanupAfter = slid;
    }

    private DateTimeOffset GetEffectiveExpiresAt(DateTimeOffset requestedExpiresAt, DateTimeOffset now)
    {
        var maxExpiresAt = now.Add(_temporaryUploadSettings.DefaultRetentionPeriod);
        var retained = requestedExpiresAt <= maxExpiresAt ? requestedExpiresAt : maxExpiresAt;

        return OctopusImportSessionExpiry.Clamp(retained, now, _temporaryUploadSettings.MaxSessionAge);
    }

    // A running import (Importing) is never cut off and a closed session is past caring; any other session past its cap takes no more work.
    private void EnsureWithinMaxAge(OctopusImportSession session)
    {
        var state = ParseState(session.State);

        if (state == OctopusImportSessionState.Importing || OctopusImportSessionStateMachine.IsTerminal(state)) return;
        if (!OctopusImportSessionExpiry.IsOverAge(session.CreatedDate, _temporaryUploadSettings.MaxSessionAge, DateTimeOffset.UtcNow)) return;

        throw new OctopusImportSessionExpiredException(session.SessionId, OctopusImportSessionExpiry.Deadline(session.CreatedDate, _temporaryUploadSettings.MaxSessionAge), _temporaryUploadSettings.MaxSessionAge);
    }

    // A chunk lease never outlives the cap: the over-age sweep does not look at leases, so a chunk still writing past the deadline would
    // have its staging directory deleted under it and leave what it writes afterwards on disk with no row pointing at it.
    private void EnsureLeaseWithinMaxAge(OctopusImportSession session, OctopusImportHistoryFetchDto historyFetch)
    {
        var deadline = OctopusImportSessionExpiry.Deadline(session.CreatedDate, _temporaryUploadSettings.MaxSessionAge);

        if (historyFetch.LeaseExpiresAt == null || historyFetch.LeaseExpiresAt <= deadline) return;

        throw new OctopusImportSessionExpiredException(session.SessionId, deadline, _temporaryUploadSettings.MaxSessionAge);
    }

    private static T Deserialize<T>(string json) where T : class
    {
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
