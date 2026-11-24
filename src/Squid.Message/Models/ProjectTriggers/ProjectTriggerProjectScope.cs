namespace Squid.Message.Models.ProjectTriggers;

/// <summary>ProjectId determines ownership. SpaceId, when supplied or injected from the header, must match the project.</summary>
public abstract class ProjectTriggerProjectScope : ISpaceScoped
{
    public int ProjectId { get; set; }

    public int? SpaceId { get; set; }
}
