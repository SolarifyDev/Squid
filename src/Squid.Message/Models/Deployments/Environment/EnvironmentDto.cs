namespace Squid.Message.Models.Deployments.Environment;

public class EnvironmentDto : IBaseModel
{
    public Guid Id { get; set; }

    public Guid SpaceId { get; set; }

    public string Slug { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int SortOrder { get; set; }

    public bool UseGuidedFailure { get; set; }

    public bool AllowDynamicInfrastructure { get; set; }

    public DateTime? LastModifiedOn { get; set; }

    public string LastModifiedBy { get; set; }
}
