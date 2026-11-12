namespace Squid.Message.Models.OctopusImport;

public class OctopusServerProbeDto
{
    public string OctopusVersion { get; set; }

    public string InstallationId { get; set; }

    /// <summary>True for a server older than 2019.1, which has no spaces: every path is unprefixed.</summary>
    public bool Spaceless { get; set; }

    public List<OctopusServerSpaceDto> Spaces { get; set; } = [];
}

public class OctopusServerSpaceDto
{
    public string Id { get; set; }

    public string Name { get; set; }

    public bool IsDefault { get; set; }
}
