namespace Squid.Message.Models.Deployments.Project;

public class CreateOrUpdateProjectModel
{
    public string Name { get; set; }
    public string Slug { get; set; }
    public bool IsDisabled { get; set; }
    public int ProjectGroupId { get; set; }
    public int LifecycleId { get; set; }
    public bool AutoCreateRelease { get; set; }
    public string Json { get; set; }
    public string IncludedLibraryVariableSetIds { get; set; }
    public bool DiscreteChannelRelease { get; set; }
    public int? ClonedFromProjectId { get; set; }
    public int SpaceId { get; set; }
    public bool AllowIgnoreChannelRules { get; set; }
}
