namespace Squid.Message.Contracts.Tentacle;

public interface IScriptService
{
    ScriptStatusResponse StartScript(StartScriptCommand command);

    ScriptStatusResponse GetStatus(ScriptStatusRequest request);

    ScriptStatusResponse CompleteScript(CompleteScriptCommand command);

    ScriptStatusResponse CancelScript(CancelScriptCommand command);
}
