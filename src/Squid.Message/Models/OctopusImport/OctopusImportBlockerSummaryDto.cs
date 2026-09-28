namespace Squid.Message.Models.OctopusImport;

public class OctopusImportBlockerSummaryDto
{
    public bool HasBlockers { get; set; }

    public int BlockerCount { get; set; }

    public int AffectedResourceCount { get; set; }

    public List<OctopusImportBlockingReasonDto> BlockingReasons { get; set; } = [];
}

public class OctopusImportBlockingReasonDto
{
    public string Code { get; set; }

    public string Message { get; set; }

    public int OccurrenceCount { get; set; }

    public int AffectedResourceCount { get; set; }
}
