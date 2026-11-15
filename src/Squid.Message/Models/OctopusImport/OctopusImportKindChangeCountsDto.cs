namespace Squid.Message.Models.OctopusImport;

/// <summary>The change counts of one resource kind (Project, Channel, Variable, Release, Deployment, …).</summary>
public class OctopusImportKindChangeCountsDto
{
    public string Kind { get; set; }

    public OctopusImportChangeCountsDto Counts { get; set; } = new();
}
