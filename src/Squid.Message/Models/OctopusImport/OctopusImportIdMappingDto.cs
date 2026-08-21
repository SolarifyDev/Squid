using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

public class OctopusImportIdMappingDto
{
    public string SourceId { get; set; }

    public string SourceType { get; set; }

    public string SourceName { get; set; }

    public string DestinationType { get; set; }

    public int DestinationId { get; set; }

    public OctopusImportResourceOutcomeState OutcomeState { get; set; }
}
