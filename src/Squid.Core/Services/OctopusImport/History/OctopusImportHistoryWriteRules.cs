using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>
/// What an update of an already-imported history unit rewrites. Scalars are rewritten when the task changed
/// (or its row is gone). Children (activity tree, logs, interruptions, events) are monotonic: incoming details
/// replace them only when they differ from the stored details, and a source without details never replaces
/// stored details; it only refreshes a summary-level unit whose task changed.
/// </summary>
public static class OctopusImportHistoryWriteRules
{
    /// <summary>
    /// An Octopus task never leaves a final state, so an incoming copy that is still running while Squid holds it finished is an older
    /// copy (a session previewed before a later import, or an export taken earlier): it never replaces the newer imported history.
    /// </summary>
    public static bool IsOlderCopy(string storedSquidState, OctopusServerTaskDto incoming)
        => TaskState.IsTerminal(storedSquidState) && !OctopusImportTaskStateNormalizer.Normalize(incoming?.State).IsTerminal;

    public static bool ScalarChanged(OctopusImportPriorMapping stored, OctopusServerTaskDto task, bool taskRowExists)
        => !taskRowExists || stored == null || !string.Equals(stored.Fingerprint, OctopusImportFingerprint.ForTask(task), StringComparison.Ordinal);

    public static bool ReplaceChildren(OctopusImportPriorMapping stored, string incomingDetailFingerprint, bool scalarChanged, bool taskRowExists)
    {
        if (!taskRowExists || stored == null) return true;

        if (!string.IsNullOrWhiteSpace(incomingDetailFingerprint)) return !string.Equals(incomingDetailFingerprint, stored.DetailFingerprint, StringComparison.Ordinal);

        return stored.DetailFingerprint == null && (scalarChanged || stored.Fingerprint == null);
    }
}
