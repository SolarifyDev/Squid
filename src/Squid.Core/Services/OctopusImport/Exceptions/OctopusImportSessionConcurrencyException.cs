namespace Squid.Core.Services.OctopusImport.Exceptions;

public class OctopusImportSessionConcurrencyException : Exception
{
    public OctopusImportSessionConcurrencyException(Guid sessionId)
        : base($"Octopus import session '{sessionId}' was changed by another operation.")
    {
        SessionId = sessionId;
    }

    public Guid SessionId { get; }
}
