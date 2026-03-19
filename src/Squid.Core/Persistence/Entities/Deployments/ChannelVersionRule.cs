namespace Squid.Core.Persistence.Entities.Deployments;

public class ChannelVersionRule : IEntity<int>
{
    public int Id { get; set; }

    public int ChannelId { get; set; }

    /// <summary>
    /// Comma-separated list of deployment action names this rule applies to.
    /// Empty or null means the rule applies to all actions.
    /// </summary>
    public string ActionNames { get; set; }

    /// <summary>
    /// SemVer version range in NuGet interval notation, e.g. "[1.0,2.0)", "(,3.0]".
    /// Empty or null means any version is accepted.
    /// </summary>
    public string VersionRange { get; set; }

    /// <summary>
    /// Regex pattern that must match the pre-release tag portion of the version.
    /// Empty or null means any pre-release tag (or none) is accepted.
    /// </summary>
    public string PreReleaseTag { get; set; }

    public int SortOrder { get; set; }
}
