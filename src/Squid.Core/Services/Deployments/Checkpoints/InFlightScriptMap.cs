using System.Text.Json;
using System.Text.Json.Serialization;

namespace Squid.Core.Services.Deployments.Checkpoints;

/// <summary>
/// Pure read/modify helpers for the deployment checkpoint's in-flight-scripts
/// list — the JSON array of <c>{ "m": machineId, "s": stepName, "a": actionName,
/// "t": scriptTicket }</c> entries stored on
/// <see cref="Persistence.Entities.Deployments.DeploymentExecutionCheckpoint.InFlightScriptsJson"/>.
///
/// <para>It records the ScriptTicket of a script dispatched to a Halibut agent
/// but not yet observed to completion, so a resumed deployment can re-attach to
/// a still-running script (probe the agent with the same ticket) instead of
/// launching a duplicate. The entry is keyed by the <see cref="DispatchSlot"/>
/// (machine + step + action), NOT by the machine alone: a parallel batch can
/// dispatch MORE THAN ONE script to the SAME machine at once (two
/// <c>StartWithPrevious</c> steps sharing a target role), and a machine-only key
/// would let the second dispatch re-attach to the first's ticket and skip its
/// own script. Scoping to the dispatch unit keeps concurrent same-machine
/// dispatches independent — live AND on resume (each re-attaches to its own
/// ticket).</para>
///
/// <para>No I/O or locking here — the store layer owns persistence and the
/// per-task write serialisation; this type is pure so the add/remove/lookup
/// semantics are unit-testable in isolation.</para>
/// </summary>
public static class InFlightScriptMap
{
    private sealed record Entry(
        [property: JsonPropertyName("m")] int MachineId,
        [property: JsonPropertyName("s")] string StepName,
        [property: JsonPropertyName("a")] string ActionName,
        [property: JsonPropertyName("t")] string Ticket);

    /// <summary>Record (or replace) the in-flight ticket for one dispatch slot.</summary>
    public static string Add(string json, DispatchSlot slot, string scriptTicket)
    {
        var entries = Parse(json);

        entries.RemoveAll(e => Matches(e, slot));
        entries.Add(new Entry(slot.MachineId, slot.StepName, slot.ActionName, scriptTicket));

        return Serialize(entries);
    }

    /// <summary>Drop a dispatch slot's in-flight ticket once its script is
    /// observed to completion. Removing an absent slot is a no-op.</summary>
    public static string Remove(string json, DispatchSlot slot)
    {
        var entries = Parse(json);

        entries.RemoveAll(e => Matches(e, slot));

        return Serialize(entries);
    }

    /// <summary>The recorded in-flight ticket for a dispatch slot, or
    /// <see langword="null"/> if none.</summary>
    public static string? TryGet(string json, DispatchSlot slot)
        => Parse(json).FirstOrDefault(e => Matches(e, slot))?.Ticket;

    private static bool Matches(Entry entry, DispatchSlot slot)
        => entry.MachineId == slot.MachineId && entry.StepName == slot.StepName && entry.ActionName == slot.ActionName;

    private static List<Entry> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();

        try
        {
            return JsonSerializer.Deserialize<List<Entry>>(json) ?? new();
        }
        catch (JsonException)
        {
            // A malformed column (manual edit, partial write) OR a row written in
            // the legacy machine-keyed object shape by an older server must not
            // crash the deploy — treat it as empty and let the executor
            // re-dispatch fresh.
            return new();
        }
    }

    private static string Serialize(List<Entry> entries) => JsonSerializer.Serialize(entries);
}
