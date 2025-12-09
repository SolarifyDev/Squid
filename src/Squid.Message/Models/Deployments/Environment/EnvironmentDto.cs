namespace Squid.Message.Models.Deployments.Environment;

public class EnvironmentDto : IBaseModel
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
