namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>A server fetch was asked for on a session opened from an export file; a session has exactly one source.</summary>
public class OctopusImportSessionNotServerSourceException : InvalidOperationException
{
    public OctopusImportSessionNotServerSourceException(Guid sessionId) : base($"Octopus import session {sessionId} was opened from an export file. Connect to the Octopus server to start a new session that reads from it.")
    {
        SessionId = sessionId;
    }

    public Guid SessionId { get; }
}
