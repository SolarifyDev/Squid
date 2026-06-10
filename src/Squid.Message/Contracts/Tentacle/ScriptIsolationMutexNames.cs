namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// Single source of truth for the agent-side script-isolation mutex name passed
/// as <see cref="StartScriptCommand.IsolationMutexName"/>.
///
/// <para>On the Tentacle, FullIsolation scripts that share a mutex name serialise
/// behind one writer lock; scripts with different names can run concurrently.
/// Squid uses a <b>machine-scoped</b> name so EVERY FullIsolation dispatch to a
/// machine — deployment OR upgrade — serialises on that machine, preventing two
/// scripts from stomping the same target (the safe per-machine default).
/// Because one Tentacle process serves exactly one machine, this is equivalent to
/// a single per-agent lock, but the explicit, shared name makes the
/// cross-deployment and deploy-vs-upgrade serialisation intentional and visible
/// in agent logs — versus the implicit <c>null → "default"</c> fallback that the
/// dispatch code previously relied on by accident.</para>
/// </summary>
public static class ScriptIsolationMutexNames
{
    /// <summary>
    /// The machine-scoped isolation mutex name. Every deployment / upgrade script
    /// dispatched to <paramref name="machineId"/> uses this exact value, so they
    /// all serialise behind one writer lock on that machine's Tentacle.
    /// </summary>
    public static string ForMachine(int machineId) => $"squid/machine/{machineId}";
}
