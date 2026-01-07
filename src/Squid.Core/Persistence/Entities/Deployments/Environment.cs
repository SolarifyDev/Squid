namespace Squid.Core.Persistence.Entities.Deployments;

public class Environment : IEntity<int>
{
    public int Id { get; set; }

    public int SpaceId { get; set; }

    public string Slug { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SortOrder { get; set; }

    public bool UseGuidedFailure { get; set; }

    public bool AllowDynamicInfrastructure { get; set; }

    public DateTimeOffset? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }
}
