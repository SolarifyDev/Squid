namespace Squid.Message.Models.Deployments.Release;

public class SelectedPackageDto
{
    public string ActionName { get; set; }
    public string PackageReferenceName { get; set; } = string.Empty;
    public string Version { get; set; }
}
