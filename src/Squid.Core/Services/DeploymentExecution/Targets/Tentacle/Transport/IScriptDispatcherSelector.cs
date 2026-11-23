using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Tentacle;

/// <summary>
/// Picks the <see cref="IScriptDispatcher"/> for a script. The choice is a pure function of what the agent
/// ADVERTISED (<see cref="MachineRuntimeCapabilities.SupportsScriptServiceV2"/>, filled by health checks) —
/// never of trying a version and catching the failure: on Halibut an agent without V2 answers a V2 call with
/// a plain transport exception that the transient-failure classifier treats as a blip, so "try V2, fall back"
/// would pause deployments to every V1 agent instead of dispatching V1.
/// </summary>
public interface IScriptDispatcherSelector : IScopedDependency
{
    /// <summary>
    /// The dispatcher for a NEW dispatch to the machine: V2 only when its cached capabilities advertise it.
    /// A cold or unknown cache is V1, which every shipped agent speaks. The cache is process-local — filled
    /// by the health checks that run on this pod and hydrated from the persisted capabilities at startup —
    /// so a pod that has not seen the machine's capabilities simply dispatches V1, which is always safe.
    /// </summary>
    IScriptDispatcher ForMachine(int machineId);

    /// <summary>
    /// The dispatcher for a script that was ALREADY dispatched with <paramref name="version"/> (resume):
    /// the recorded version wins over whatever the agent advertises now. Throws
    /// <see cref="UnsupportedScriptProtocolException"/> for a version no registered dispatcher speaks.
    /// </summary>
    IScriptDispatcher ForVersion(ScriptProtocolVersion version);

    /// <summary>Whether the machine's cached capabilities currently advertise <paramref name="version"/>; V1 always (every agent speaks it).</summary>
    bool Advertises(int machineId, ScriptProtocolVersion version);
}

public sealed class ScriptDispatcherSelector : IScriptDispatcherSelector
{
    private readonly IReadOnlyDictionary<ScriptProtocolVersion, IScriptDispatcher> _dispatchers;
    private readonly IMachineRuntimeCapabilitiesCache _capabilitiesCache;

    public ScriptDispatcherSelector(IEnumerable<IScriptDispatcher> dispatchers, IMachineRuntimeCapabilitiesCache capabilitiesCache = null)
    {
        _dispatchers = dispatchers.ToDictionary(d => d.Version);
        _capabilitiesCache = capabilitiesCache;
    }

    public IScriptDispatcher ForMachine(int machineId)
        => ForVersion(Advertises(machineId, ScriptProtocolVersion.V2) && _dispatchers.ContainsKey(ScriptProtocolVersion.V2) ? ScriptProtocolVersion.V2 : ScriptProtocolVersion.V1);

    public IScriptDispatcher ForVersion(ScriptProtocolVersion version)
        => _dispatchers.TryGetValue(version, out var dispatcher) ? dispatcher : throw new UnsupportedScriptProtocolException(version.ToString());

    public bool Advertises(int machineId, ScriptProtocolVersion version)
        => version == ScriptProtocolVersion.V1 || (version == ScriptProtocolVersion.V2 && (_capabilitiesCache?.TryGet(machineId)?.SupportsScriptServiceV2 ?? false));
}
