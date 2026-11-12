namespace Squid.Message.Enums.OctopusImport;

/// <summary>Where a REST history fetch stands inside the current project.</summary>
public enum OctopusHistoryFetchPhase
{
    Releases = 0,
    Deployments = 1,
    Done = 2
}
