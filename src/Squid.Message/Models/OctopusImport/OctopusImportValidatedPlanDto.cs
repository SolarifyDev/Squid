namespace Squid.Message.Models.OctopusImport;

public class OctopusImportValidatedPlanDto
{
    public OctopusImportPreviewPlanDto PreviewPlan { get; set; }

    public OctopusImportValidationResultDto Validation { get; set; }
}
