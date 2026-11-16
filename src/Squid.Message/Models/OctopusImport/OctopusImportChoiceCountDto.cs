using Squid.Message.Enums.OctopusImport;

namespace Squid.Message.Models.OctopusImport;

/// <summary>How many resources a plan offers for one reason, and how many of them are selected (followers not counted).</summary>
public class OctopusImportChoiceCountDto
{
    public OctopusImportChoiceReason Reason { get; set; }

    public int Total { get; set; }

    public int Selected { get; set; }
}
