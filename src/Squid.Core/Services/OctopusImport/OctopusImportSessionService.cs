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

    Task<OctopusImportSessionDto> GetSessionAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default);

    Task<OctopusImportSessionDto> UpdatePayloadAndTransitionAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        string redactedNormalizedDataJson = null,
        string validatedPlanJson = null,
        CancellationToken ct = default);

    Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default);

    Task<OctopusImportSessionDto> RecordResultAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState terminalState,
        OctopusImportSessionResultDto result,
        CancellationToken ct = default);

    Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default);
}

public class OctopusImportSessionService : IOctopusImportSessionService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IOctopusImportSessionDataProvider _dataProvider;
    private readonly ICurrentUser _currentUser;

    public OctopusImportSessionService(IOctopusImportSessionDataProvider dataProvider, ICurrentUser currentUser)
    {
        _dataProvider = dataProvider;
        _currentUser = currentUser;
    }

    public async Task<OctopusImportSessionDto> CreateSessionAsync(
        int destinationSpaceId,
        OctopusImportSourceSummaryDto sourceSummary,
        DateTimeOffset expiresAt,
        CancellationToken ct = default)
    {
        var ownerUserId = GetCurrentUserId();
        var now = DateTimeOffset.UtcNow;

        var session = new OctopusImportSession
        {
            SessionId = Guid.NewGuid(),
            DestinationSpaceId = destinationSpaceId,
            OwnerUserId = ownerUserId,
            State = OctopusImportSessionState.Uploaded.ToString(),
            SourceSummaryJson = Serialize(OctopusImportRedaction.RedactDto(sourceSummary ?? new OctopusImportSourceSummaryDto())),
            DataVersion = Guid.NewGuid().ToByteArray(),
            ExpiresAt = expiresAt,
            LastStateChangedAt = now
        };

        await _dataProvider.AddSessionAsync(session, ct: ct).ConfigureAwait(false);

        return Map(session);
    }

    public async Task<OctopusImportSessionDto> GetSessionAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
    {
        return Map(await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false));
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

        session.State = newState.ToString();
        session.LastStateChangedAt = DateTimeOffset.UtcNow;
        session.RedactedNormalizedDataJson = redactedNormalizedDataJson == null
            ? session.RedactedNormalizedDataJson
            : OctopusImportRedaction.RedactJson(redactedNormalizedDataJson);
        session.ValidatedPlanJson = validatedPlanJson == null
            ? session.ValidatedPlanJson
            : OctopusImportRedaction.RedactJson(validatedPlanJson);

        if (OctopusImportSessionStateMachine.IsTerminal(newState))
            session.CompletedAt = session.LastStateChangedAt;

        await _dataProvider.UpdateSessionAsync(session, ct: ct).ConfigureAwait(false);

        return Map(session);
    }

    public async Task<bool> TryStartConfirmationAsync(Guid sessionId, int destinationSpaceId, CancellationToken ct = default)
    {
        var ownerUserId = GetCurrentUserId();
        var session = await _dataProvider.GetSessionAsync(sessionId, ownerUserId, destinationSpaceId, ct).ConfigureAwait(false);

        if (session == null)
            throw new OctopusImportSessionNotFoundException(sessionId);

        return await _dataProvider.TryStartConfirmationAsync(sessionId, ownerUserId, destinationSpaceId, ct).ConfigureAwait(false);
    }

    public async Task<OctopusImportSessionDto> RecordResultAsync(
        Guid sessionId,
        int destinationSpaceId,
        OctopusImportSessionState terminalState,
        OctopusImportSessionResultDto result,
        CancellationToken ct = default)
    {
        if (!OctopusImportSessionStateMachine.IsTerminal(terminalState))
            throw new ArgumentException($"State '{terminalState}' is not terminal.", nameof(terminalState));

        var session = await GetOwnedSessionAsync(sessionId, destinationSpaceId, ct).ConfigureAwait(false);
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

        await _dataProvider.UpdateSessionAsync(session, ct: ct).ConfigureAwait(false);

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

    private int GetCurrentUserId()
    {
        if (_currentUser.Id == null)
            throw new UnauthorizedAccessException("Octopus import sessions require an authenticated user.");

        return _currentUser.Id.Value;
    }

    private static OctopusImportSessionDto Map(OctopusImportSession session)
    {
        if (session == null)
            return null;

        return new OctopusImportSessionDto
        {
            SessionId = session.SessionId,
            DestinationSpaceId = session.DestinationSpaceId,
            OwnerUserId = session.OwnerUserId,
            State = ParseState(session.State),
            SourceSummary = OctopusImportRedaction.RedactDto(Deserialize<OctopusImportSourceSummaryDto>(session.SourceSummaryJson)),
            Result = OctopusImportRedaction.RedactDto(Deserialize<OctopusImportSessionResultDto>(session.ResultJson)),
            ExpiresAt = session.ExpiresAt,
            CompletedAt = session.CompletedAt,
            LastStateChangedAt = session.LastStateChangedAt
        };
    }

    private static OctopusImportSessionState ParseState(string state)
    {
        return Enum.TryParse<OctopusImportSessionState>(state, ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown Octopus import session state.");
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    private static T Deserialize<T>(string json) where T : class
    {
        return string.IsNullOrWhiteSpace(json)
            ? null
            : JsonSerializer.Deserialize<T>(json, JsonOptions);
    }
}
