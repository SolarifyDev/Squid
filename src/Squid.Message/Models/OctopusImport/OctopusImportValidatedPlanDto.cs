namespace Squid.Message.Models.OctopusImport;

public class OctopusImportValidatedPlanDto
{
    public OctopusImportPreviewPlanDto PreviewPlan { get; set; }

    public OctopusImportValidationResultDto Validation { get; set; }

    /// <summary>The project selection the plan was validated with (null = whole space). Confirmation refuses a plan whose selection is no longer the session's.</summary>
    public List<string> SelectedProjectIds { get; set; }
}
