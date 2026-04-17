using Squid.Message.Contracts.Tentacle;
using Squid.Tentacle.Abstractions;

namespace Squid.Tentacle.Core;

public sealed class BackendScriptServiceAdapter : IScriptService
{
    private readonly ITentacleScriptBackend _backend;

    public BackendScriptServiceAdapter(ITentacleScriptBackend backend)
    {
        _backend = backend;
    }

    public ScriptStatusResponse StartScript(StartScriptCommand command) => _backend.StartScript(command);

    public ScriptStatusResponse GetStatus(ScriptStatusRequest request) => _backend.GetStatus(request);

    public ScriptStatusResponse CompleteScript(CompleteScriptCommand command) => _backend.CompleteScript(command);

    public ScriptStatusResponse CancelScript(CancelScriptCommand command) => _backend.CancelScript(command);
}
