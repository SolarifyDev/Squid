using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Abstractions;

public interface ITentacleScriptBackend
{
    ScriptTicket StartScript(StartScriptCommand command);

    ScriptStatusResponse GetStatus(ScriptStatusRequest request);

    ScriptStatusResponse CompleteScript(CompleteScriptCommand command);

    ScriptStatusResponse CancelScript(CancelScriptCommand command);
}
