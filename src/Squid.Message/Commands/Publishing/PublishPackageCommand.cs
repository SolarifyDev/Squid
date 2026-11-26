using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Publishing;

/// <summary>
/// Publishes a built-in package as an app: picks a recipe, creates (or reuses) the project and its deployment process, creates the release for this version and
/// starts the deployment. Every permission the separate steps need (ReleaseEdit is for refreshing the variables of a release that already exists for this version) is required up front, so a person who cannot do one of them is refused before anything is written.
/// </summary>
[RequiresPermission(Permission.FeedView)]
[RequiresPermission(Permission.ProjectCreate)]
[RequiresPermission(Permission.ProcessEdit)]
[RequiresPermission(Permission.ReleaseCreate)]
[RequiresPermission(Permission.ReleaseEdit)]
[RequiresPermission(Permission.DeploymentCreate)]
public class PublishPackageCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public string PackageId { get; set; }

    public string Version { get; set; }

    /// <summary>Optional: the recipe to use. Without it, the recipe <c>squid.yaml</c> names, else the best fit.</summary>
    public string RecipeKind { get; set; }

    /// <summary>Optional: the name of the project when it is created; the package id when absent. Ignored once the package has its project.</summary>
    public string ProjectName { get; set; }

    public int EnvironmentId { get; set; }

    public List<string> TargetRoles { get; set; } = new();

    /// <summary>The values the chosen recipe asked for, by input name (the static-site and Dockerfile recipes ask for <c>hostPort</c>), kept for the environment being published to.</summary>
    public Dictionary<string, string> Inputs { get; set; } = new();

    /// <summary>Optional: used only when the project is created; the space's first project group when absent.</summary>
    public int? ProjectGroupId { get; set; }

    /// <summary>Optional: used only when the project is created; the space's first lifecycle that lists the environment when absent.</summary>
    public int? LifecycleId { get; set; }
}

public class PublishPackageResponse : SquidResponse<PublishPackageResponseData>
{
}

public class PublishPackageResponseData
{
    public int ProjectId { get; set; }

    public bool ProjectCreated { get; set; }

    public bool ProcessUpdated { get; set; }

    public string RecipeKind { get; set; }

    public int ReleaseId { get; set; }

    public int DeploymentId { get; set; }

    public int TaskId { get; set; }
}
