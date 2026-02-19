namespace Squid.Core.Persistence.Entities.Deployments;

public class ReleaseSelectedPackage : IEntity<int>
{
    public int Id { get; set; }
    public int ReleaseId { get; set; }
    public string ActionName { get; set; }
    public string Version { get; set; }
}
