using Squid.Message.Contracts.Tentacle.V2;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Core;

/// <summary>
/// The adapter handed to the Halibut host for a backend that also implements <see cref="IScriptServiceV2"/>.
/// The host registers V2 only when the script service it receives implements it, so V1-only backends never
/// expose (or advertise) a V2 they cannot serve.
/// </summary>
public sealed class BackendScriptServiceV2Adapter : BackendScriptServiceAdapter, IScriptServiceV2
{
    private readonly IScriptServiceV2 _v2;

    public BackendScriptServiceV2Adapter(ITentacleScriptBackend backend, IScriptServiceV2 v2) : base(backend)
    {
        _v2 = v2;
    }

    public ScriptStatusResponseV2 StartScript(StartScriptCommandV2 command) => _v2.StartScript(command);

    public ScriptStatusResponseV2 GetStatus(ScriptStatusRequestV2 request) => _v2.GetStatus(request);

    public ScriptStatusResponseV2 CancelScript(CancelScriptCommandV2 command) => _v2.CancelScript(command);

    public CompleteScriptResponseV2 CompleteScript(CompleteScriptCommandV2 command) => _v2.CompleteScript(command);
}
