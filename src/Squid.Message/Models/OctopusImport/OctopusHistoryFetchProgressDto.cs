using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusHistoryFetchProgressDto
{
    public OctopusHistoryFetchPhase Phase { get; set; }

    public int ProjectIndex { get; set; }

    public int ProjectCount { get; set; }

    /// <summary>The catalog kind being read, of <see cref="CatalogKindCount"/>, while the phase is Catalog.</summary>
    public int CatalogIndex { get; set; }

    public int CatalogKindCount { get; set; }

    /// <summary>Items of the current paged phase already fetched: the current catalog kind's resources, or the current project's releases or deployments. 0 in the other phases.</summary>
    public int PhaseItemsDone { get; set; }

    /// <summary>How many items Octopus reports for what <see cref="PhaseItemsDone"/> counts; null while unknown (before the phase's first page) and in the other phases.</summary>
    public int? PhaseItemsTotal { get; set; }

    public OctopusHistoryFetchCountsDto Counts { get; set; } = new();

    public long TotalBytes { get; set; }

    public bool Done { get; set; }
}
