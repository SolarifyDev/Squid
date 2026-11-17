namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// The confirmation attempt no longer holds its session (the recovery sweep returned it to Validated, or another attempt started):
/// whatever it was about to write must not land, so the write is refused and its transaction rolls back.
/// </summary>
public class OctopusImportConfirmationFencedException(Guid sessionId, Guid attemptId) : InvalidOperationException($"Octopus import confirmation attempt '{attemptId}' no longer holds session '{sessionId}'.")
{
    public Guid SessionId { get; } = sessionId;

    public Guid AttemptId { get; } = attemptId;
}
