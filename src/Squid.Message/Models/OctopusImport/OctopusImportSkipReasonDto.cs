namespace Squid.Message.Models.OctopusImport;

/// <summary>Why a resource is not imported: the diagnostic that decided it.</summary>
public class OctopusImportSkipReasonDto
{
    public string Code { get; set; }

    public string Message { get; set; }
}
