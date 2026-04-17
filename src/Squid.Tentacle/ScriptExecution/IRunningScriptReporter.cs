namespace Squid.Tentacle.ScriptExecution;

/// <summary>
/// Asks every script backend whether a given ticket is currently running. Used
/// by cleanup / self-heal / workspace GC code paths to avoid removing state
/// for a live script — the bare "24h age" heuristic has a race when a script
/// legitimately runs longer than the retention window.
///
/// Multiple implementations coexist (LocalScriptService for bash/pwsh,
/// ScriptPodService for K8s). Consumers inject <c>IEnumerable&lt;IRunningScriptReporter&gt;</c>
/// and treat "any reporter says it's live" as a veto.
/// </summary>
public interface IRunningScriptReporter
{
    /// <summary>
    /// Returns true if a script with the given ticket id is currently being
    /// tracked (running, pending an isolation mutex, or in a recently-terminal
    /// state that has not yet been drained by CompleteScript).
    /// </summary>
    bool IsRunningScript(string ticketId);
}
