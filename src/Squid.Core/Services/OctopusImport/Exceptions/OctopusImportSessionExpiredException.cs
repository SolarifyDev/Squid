namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// The session reached its maximum age (<see cref="OctopusImportSessionExpiry"/>): it takes no more work, even before the cleanup sweep
/// closes it. Not resumable; the caller starts a new session.
/// </summary>
public class OctopusImportSessionExpiredException : InvalidOperationException
{
    public OctopusImportSessionExpiredException(Guid sessionId, DateTimeOffset deadline, TimeSpan maxAge) : base(OctopusImportSessionExpiry.MaxAgeReachedMessage(maxAge))
    {
        SessionId = sessionId;
        Deadline = deadline;
    }

    public Guid SessionId { get; }

    public DateTimeOffset Deadline { get; }
}
