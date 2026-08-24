using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

[RequiresPermission(Permission.ProjectCreate)]
public class ExtractOctopusImportCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }
}

public class ExtractOctopusImportResponse : SquidResponse<ExtractOctopusImportResponseData>
{
}

public class ExtractOctopusImportResponseData
{
    public OctopusImportSessionDto Session { get; set; }

    public OctopusImportExtractionResultDto Extraction { get; set; }
}
