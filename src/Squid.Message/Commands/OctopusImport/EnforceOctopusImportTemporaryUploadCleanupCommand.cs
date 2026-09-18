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

    public int InterruptedImportsFailed { get; set; }

    public int Scanned { get; set; }

    public int Cleaned { get; set; }

    public int Failed { get; set; }
}
