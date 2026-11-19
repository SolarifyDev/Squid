using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

/// <summary>
/// System-triggered sweep: returns every Importing session whose confirmation heartbeat stopped (its server died before it committed)
/// to Validated, so it can be imported again. Not exposed via a controller.
/// </summary>
public class RecoverInterruptedOctopusImportConfirmationsCommand : ICommand
{
}

public class RecoverInterruptedOctopusImportConfirmationsResponse : SquidResponse<RecoverInterruptedOctopusImportConfirmationsResponseData>
{
}

public class RecoverInterruptedOctopusImportConfirmationsResponseData
{
    public int Recovered { get; set; }
}
