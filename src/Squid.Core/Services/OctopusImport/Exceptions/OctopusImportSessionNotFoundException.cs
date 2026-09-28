namespace Squid.Core.Services.OctopusImport.Exceptions;

public class OctopusImportSessionNotFoundException : Exception
{
    public OctopusImportSessionNotFoundException(Guid sessionId)
        : base($"Octopus import session '{sessionId}' was not found.")
    {
        SessionId = sessionId;
    }

    public Guid SessionId { get; }
}
