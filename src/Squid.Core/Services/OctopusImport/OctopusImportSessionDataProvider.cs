using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public interface IOctopusImportSessionDataProvider : IScopedDependency
{
    Task AddSessionAsync(OctopusImportSession session, bool forceSave = true, CancellationToken ct = default);

    Task<OctopusImportSession> GetSessionAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default);

    Task UpdateSessionAsync(OctopusImportSession session, bool forceSave = true, CancellationToken ct = default);

    Task<int> TransitionStateAsync(
        Guid sessionId,
        int ownerUserId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        CancellationToken ct = default);

    Task<bool> TryStartConfirmationAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default);

    Task<int> ExpireSessionsAsync(DateTimeOffset now, CancellationToken ct = default);
}

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

    public async Task UpdateSessionAsync(OctopusImportSession session, bool forceSave = true, CancellationToken ct = default)
    {
        session.DataVersion = Guid.NewGuid().ToByteArray();
        await _repository.UpdateAsync(session, ct).ConfigureAwait(false);

        if (forceSave)
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public Task<int> TransitionStateAsync(
        Guid sessionId,
        int ownerUserId,
        int destinationSpaceId,
        OctopusImportSessionState expectedState,
        OctopusImportSessionState newState,
        CancellationToken ct = default)
    {
        OctopusImportSessionStateMachine.EnsureValidTransition(expectedState, newState);

        var now = DateTimeOffset.UtcNow;
        var dataVersion = Guid.NewGuid().ToByteArray();
        var completedAt = OctopusImportSessionStateMachine.IsTerminal(newState) ? now : (DateTimeOffset?)null;

        return _repository.ExecuteUpdateAsync<OctopusImportSession>(
            s => s.SessionId == sessionId &&
                 s.OwnerUserId == ownerUserId &&
                 s.DestinationSpaceId == destinationSpaceId &&
                 s.State == expectedState.ToString(),
            setters => setters
                .SetProperty(s => s.State, newState.ToString())
                .SetProperty(s => s.DataVersion, dataVersion)
                .SetProperty(s => s.LastStateChangedAt, now)
                .SetProperty(s => s.LastModifiedDate, now)
                .SetProperty(s => s.CompletedAt, completedAt),
            ct);
    }

    public async Task<bool> TryStartConfirmationAsync(Guid sessionId, int ownerUserId, int destinationSpaceId, CancellationToken ct = default)
    {
        var rowsAffected = await TransitionStateAsync(
            sessionId,
            ownerUserId,
            destinationSpaceId,
            OctopusImportSessionState.Validated,
            OctopusImportSessionState.Importing,
            ct).ConfigureAwait(false);

        return rowsAffected == 1;
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
                .SetProperty(s => s.CompletedAt, now),
            ct);
    }
}
