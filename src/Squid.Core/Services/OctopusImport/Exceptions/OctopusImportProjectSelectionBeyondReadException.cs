namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>A server session already reading its chosen projects was asked for a project it does not read: connect again to add projects.</summary>
public class OctopusImportProjectSelectionBeyondReadException : InvalidOperationException
{
    public OctopusImportProjectSelectionBeyondReadException(Guid sessionId) : base($"Octopus import session {sessionId} already reads its chosen projects from Octopus, so it can only leave some of them out. Connect again to choose other projects.")
    {
        SessionId = sessionId;
    }

    public Guid SessionId { get; }
}
