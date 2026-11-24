namespace Squid.Message.Models.ProjectTriggers;

public static class ProjectTriggerActions
{
    public const string CreateRelease = "CreateRelease";

    public const string AutoDeploy = "AutoDeploy";

    public const string DeployLatestRelease = "DeployLatestRelease";

    public const string DeployNewRelease = "DeployNewRelease";

    public const string DeployLatestReleaseToEnvironment = "DeployLatestReleaseToEnvironment";
}
