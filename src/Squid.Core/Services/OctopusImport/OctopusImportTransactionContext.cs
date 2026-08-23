using System.Data;

namespace Squid.Core.Services.OctopusImport;

/// <summary>
/// Carries the immutable boundary information for a single import confirmation transaction.
///
/// The context is intentionally small: it identifies the import session, the destination space,
/// and any later concurrency/isolation override without exposing persistence details outside the
/// transaction executor.
/// </summary>
public sealed class OctopusImportTransactionContext
{
    public OctopusImportTransactionContext(
        Guid sessionId,
        int destinationSpaceId,
        IsolationLevel? isolationLevel = null,
        DateTimeOffset? startedAt = null)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session ID is required.", nameof(sessionId));
        if (destinationSpaceId <= 0)
            throw new ArgumentOutOfRangeException(nameof(destinationSpaceId), destinationSpaceId, "Destination space ID must be positive.");

        SessionId = sessionId;
        DestinationSpaceId = destinationSpaceId;
        IsolationLevel = isolationLevel;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
    }

    public Guid SessionId { get; }

    public int DestinationSpaceId { get; }

    public IsolationLevel? IsolationLevel { get; }

    public DateTimeOffset StartedAt { get; }
}
