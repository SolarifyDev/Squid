using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Models.OctopusImport;
using Squid.Message.Response;

namespace Squid.Message.Commands.OctopusImport;

[RequiresPermission(Permission.ProjectCreate)]
public class ValidateOctopusImportCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public Guid SessionId { get; set; }

    public OctopusImportPreviewPlanDto PreviewPlan { get; set; }
}

public class ValidateOctopusImportResponse : SquidResponse<ValidateOctopusImportResponseData>
{
}

public class ValidateOctopusImportResponseData
{
    public OctopusImportSessionDto Session { get; set; }

    public OctopusImportPreviewPlanDto PreviewPlan { get; set; }

    public OctopusImportValidationResultDto Validation { get; set; }
}
