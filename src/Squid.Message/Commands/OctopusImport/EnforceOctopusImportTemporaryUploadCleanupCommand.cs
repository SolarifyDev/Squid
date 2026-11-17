using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>
/// System-triggered sweep for Octopus import temporary uploads. Not exposed via
/// a controller; runs cross-space under the internal service identity.
/// </summary>
public class EnforceOctopusImportTemporaryUploadCleanupCommand : ICommand
{
}

public class EnforceOctopusImportTemporaryUploadCleanupResponse
    : SquidResponse<EnforceOctopusImportTemporaryUploadCleanupResponseData>
{
}

public class EnforceOctopusImportTemporaryUploadCleanupResponseData
{
    public int ExpiredSessions { get; set; }

    /// <summary>
    /// Always 0: an interrupted import is no longer failed by this sweep; <see cref="RecoverInterruptedOctopusImportConfirmationsCommand"/>
    /// returns it to Validated instead. Kept so existing readers of the response keep working.
    /// </summary>
    public int InterruptedImportsFailed { get; set; }

    public int Scanned { get; set; }

    public int Cleaned { get; set; }

    public int Failed { get; set; }

    /// <summary>Sessions closed because they reached the maximum session age (counted separately from <see cref="ExpiredSessions"/>).</summary>
    public int OverAgeSessions { get; set; }
}
