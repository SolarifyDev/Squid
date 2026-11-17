namespace Squid.Message.Models.OctopusImport;

/// <summary>How many resources of one kind are not imported for one reason; <see cref="Message"/> is one example of the reason.</summary>
public class OctopusImportSkipReasonCountDto
{
    public string ResourceType { get; set; }

    public string Code { get; set; }

    public string Message { get; set; }

    public int Count { get; set; }
}
