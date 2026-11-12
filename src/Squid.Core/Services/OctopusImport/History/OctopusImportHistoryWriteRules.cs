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
    public static bool ScalarChanged(OctopusImportPriorMapping stored, OctopusServerTaskDto task, bool taskRowExists)
        => !taskRowExists || stored == null || !string.Equals(stored.Fingerprint, OctopusImportFingerprint.ForTask(task), StringComparison.Ordinal);

    public static bool ReplaceChildren(OctopusImportPriorMapping stored, string incomingDetailFingerprint, bool scalarChanged, bool taskRowExists)
    {
        if (!taskRowExists || stored == null) return true;

        if (!string.IsNullOrWhiteSpace(incomingDetailFingerprint)) return !string.Equals(incomingDetailFingerprint, stored.DetailFingerprint, StringComparison.Ordinal);

        return stored.DetailFingerprint == null && (scalarChanged || stored.Fingerprint == null);
    }
}
