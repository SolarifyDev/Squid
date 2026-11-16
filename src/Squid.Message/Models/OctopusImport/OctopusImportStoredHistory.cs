namespace Squid.Message.Models.OctopusImport;

/// <summary>The task scalars an earlier import wrote, as Squid holds them now (compared with the incoming task for an Update).</summary>
public sealed record OctopusImportStoredTask(int Id, string State, DateTimeOffset QueueTime, DateTimeOffset? StartTime, DateTimeOffset? CompletedTime, string ErrorMessage, bool HasWarningsOrErrors);

/// <summary>Squid's current values of the history rows an Update would rewrite: release notes by release id, task scalars by task id.</summary>
public sealed record OctopusImportStoredHistory(IReadOnlyDictionary<int, string> ReleaseNotes, IReadOnlyDictionary<int, OctopusImportStoredTask> Tasks)
{
    public static OctopusImportStoredHistory Empty { get; } = new(new Dictionary<int, string>(), new Dictionary<int, OctopusImportStoredTask>());
}

/// <summary>
/// The history rows of a space a Squid delete left behind: releases whose project or channel, and deployments whose project, channel,
/// release or environment, no longer exist. Only these are moved onto an owner an import creates again; history whose owners exist stays put.
/// </summary>
public sealed record OctopusImportOrphanedHistory(IReadOnlySet<int> ReleaseIds, IReadOnlySet<int> DeploymentIds)
{
    public static OctopusImportOrphanedHistory Empty { get; } = new(new HashSet<int>(), new HashSet<int>());
}
