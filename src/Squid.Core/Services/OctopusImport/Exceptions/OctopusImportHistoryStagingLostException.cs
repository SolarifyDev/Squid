namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// What earlier chunks of a history fetch read from Octopus is not stored any more (the session was cleaned up, or an earlier Squid
/// version started the fetch). Resuming would build a bundle silently missing it, so the session cannot go on.
/// </summary>
public class OctopusImportHistoryStagingLostException : InvalidOperationException
{
    public const string DefaultMessage = "The data this import session already read from Octopus is no longer available (the session was cleaned up, or it was started by an earlier Squid version). Nothing was imported; start a new import session.";

    public OctopusImportHistoryStagingLostException() : base(DefaultMessage)
    {
    }

    protected OctopusImportHistoryStagingLostException(string message) : base(message)
    {
    }
}
