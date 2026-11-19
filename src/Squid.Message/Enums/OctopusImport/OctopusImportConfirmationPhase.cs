namespace Squid.Message.Enums.OctopusImport;

/// <summary>A group of resource kinds a confirmation counts progress by. Append-only: the SquidWeb mirror is ordinal.</summary>
public enum OctopusImportConfirmationPhase
{
    ProjectGroups = 0,
    Environments = 1,
    Lifecycles = 2,
    Feeds = 3,
    Accounts = 4,
    Teams = 5,
    LibraryVariableSets = 6,
    Projects = 7,
    Channels = 8,
    Variables = 9,
    Processes = 10,
    Releases = 11,
    Deployments = 12
}
