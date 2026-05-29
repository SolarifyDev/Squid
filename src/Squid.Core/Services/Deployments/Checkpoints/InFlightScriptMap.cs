using System.Text.Json;

namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// Pure read/modify helpers for the deployment checkpoint's in-flight-scripts
/// map — the JSON shape <c>{ "&lt;machineId&gt;": "&lt;scriptTicket&gt;" }</c> stored on
/// <see cref="Persistence.Entities.Deployments.DeploymentExecutionCheckpoint.InFlightScriptsJson"/>.
///
/// <para>It records the ScriptTicket of a script dispatched to a Halibut agent
/// but not yet observed to completion, so a resumed deployment can re-attach to
/// a still-running script (probe the agent with the same ticket) instead of
/// launching a duplicate. No I/O or locking here — the store layer owns
/// persistence and the per-task write serialisation; this type is pure so the
/// add/remove/lookup semantics are unit-testable in isolation.</para>
/// </summary>
public static class InFlightScriptMap
{
    /// <summary>Record (or overwrite) the in-flight ticket for a machine.</summary>
    public static string Add(string json, int machineId, string scriptTicket)
    {
        var map = Parse(json);
        map[machineId.ToString()] = scriptTicket;
        return Serialize(map);
    }

    /// <summary>Drop a machine's in-flight ticket once its script is observed
    /// to completion. Removing an absent machine is a no-op.</summary>
    public static string Remove(string json, int machineId)
    {
        var map = Parse(json);
        map.Remove(machineId.ToString());
        return Serialize(map);
    }

    /// <summary>The recorded in-flight ticket for a machine, or
    /// <see langword="null"/> if none.</summary>
    public static string? TryGet(string json, int machineId)
        => Parse(json).TryGetValue(machineId.ToString(), out var ticket) ? ticket : null;

    private static Dictionary<string, string> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (JsonException)
        {
            // A malformed column (manual edit, partial write) must not crash the
            // deploy — treat it as empty and let the executor re-dispatch fresh.
            return new();
        }
    }

    private static string Serialize(Dictionary<string, string> map) => JsonSerializer.Serialize(map);
}
