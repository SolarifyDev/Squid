namespace Squid.Message.Models.Publishing;

/// <summary>What the template applier needs to turn a plan into a project and a deployment process.</summary>
public class PlanApplication
{
    public int SpaceId { get; set; }

    public int FeedId { get; set; }

    /// <summary>The environment the app is deployed to now; the default lifecycle of a new project is one that lists it.</summary>
    public int EnvironmentId { get; set; }

    public string PackageId { get; set; }

    public string RecipeKind { get; set; }

    public string ProjectName { get; set; }

    public int? ProjectGroupId { get; set; }

    public int? LifecycleId { get; set; }

    public DeploymentPlan Plan { get; set; }
}

public class AppliedPlan
{
    public int ProjectId { get; set; }

    public int ChannelId { get; set; }

    public bool ProjectCreated { get; set; }

    /// <summary>True when an existing project's process was rebuilt because the plan changed; false when it was left as it was, and for a project just created.</summary>
    public bool ProcessUpdated { get; set; }

    /// <summary>The names of the actions that deploy the package: what a release's selected package refers to.</summary>
    public List<string> PackageActionNames { get; set; } = new();
}
