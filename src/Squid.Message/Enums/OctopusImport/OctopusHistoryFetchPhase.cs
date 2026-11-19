namespace Squid.Message.Enums.OctopusImport;

/// <summary>
/// Where an Octopus server fetch stands. Append-only (the cursor is persisted between chunks). The order a fetch runs in is
/// <see cref="Catalog"/> → for each project <see cref="ProjectConfiguration"/> → <see cref="Releases"/> → <see cref="Deployments"/>
/// → <see cref="Closure"/> → <see cref="Done"/>.
/// </summary>
public enum OctopusHistoryFetchPhase
{
    Releases = 0,
    Deployments = 1,
    Done = 2,

    /// <summary>The space-wide resources, one kind after another; the last kind (projects) lists the projects to fetch.</summary>
    Catalog = 3,

    /// <summary>The current project's own documents: process, deployment settings, variables, channels, runbooks, triggers.</summary>
    ProjectConfiguration = 4,

    /// <summary>Resources referenced by what was fetched but not staged (system teams, resources created during the fetch).</summary>
    Closure = 5
}
