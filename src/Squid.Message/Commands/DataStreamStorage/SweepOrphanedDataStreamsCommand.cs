using Squid.Message.Response;

namespace Squid.Message.Commands.DataStreamStorage;

/// <summary>
/// System-triggered sweep: removes the stored Halibut data streams of queued requests that were cancelled, timed out or never collected.
/// Not exposed via a controller.
/// </summary>
public class SweepOrphanedDataStreamsCommand : ICommand
{
}

public class SweepOrphanedDataStreamsResponse : SquidResponse<SweepOrphanedDataStreamsResponseData>
{
}

public class SweepOrphanedDataStreamsResponseData
{
    public int Removed { get; set; }
}
