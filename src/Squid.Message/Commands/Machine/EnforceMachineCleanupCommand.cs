using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

/// <summary>
/// System-triggered (recurring job) sweep that enforces each machine policy's
/// "delete unavailable deployment targets after N" cleanup behaviour. Not exposed
/// via a controller — runs cross-space under the internal service identity, so it
/// carries no permission attribute or space scope.
/// </summary>
public class EnforceMachineCleanupCommand : ICommand
{
}

public class EnforceMachineCleanupResponse : SquidResponse<EnforceMachineCleanupResponseData>
{
}

public class EnforceMachineCleanupResponseData
{
    public int Scanned { get; set; }

    public int Eligible { get; set; }

    public int Deleted { get; set; }
}
