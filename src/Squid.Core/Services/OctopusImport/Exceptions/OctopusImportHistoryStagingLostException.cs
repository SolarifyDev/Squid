namespace Squid.Core.Services.OctopusImport.Exceptions;

/// <summary>
/// The documents earlier chunks of a history fetch staged are not on this Squid server's disk any more (it restarted on temporary
/// storage, or another replica served the request). Resuming would build a bundle silently missing them, so the session cannot go on.
/// </summary>
public class OctopusImportHistoryStagingLostException : InvalidOperationException
{
    public OctopusImportHistoryStagingLostException() : base("The data this import session already read from Octopus is no longer on this Squid server (it restarted, or another Squid replica answered). Nothing was imported; start a new import session. Operators running more than one replica or on temporary storage: set OctopusImport:TemporaryUploads:RootPath (OctopusImport__TemporaryUploads__RootPath) to a persistent volume shared by every replica.")
    {
    }
}
