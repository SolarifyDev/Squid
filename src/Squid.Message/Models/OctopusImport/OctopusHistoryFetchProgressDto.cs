using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusHistoryFetchProgressDto
{
    public OctopusHistoryFetchPhase Phase { get; set; }

    public int ProjectIndex { get; set; }

    public int ProjectCount { get; set; }

    public OctopusHistoryFetchCountsDto Counts { get; set; } = new();

    public long TotalBytes { get; set; }

    public bool Done { get; set; }
}
