using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>One item a REST fetch left out: the Octopus project it belongs to (null outside a project), the request that failed, and why.</summary>
public class OctopusHistoryFetchSkippedItemDto
{
    public string ProjectId { get; set; }

    /// <summary>The relative request path and query (never the host or the key).</summary>
    public string Path { get; set; }

    public OctopusHistoryFetchSkipReason Reason { get; set; }
}
