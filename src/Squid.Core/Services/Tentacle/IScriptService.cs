using Squid.Core.Commands.Tentacle;

namespace Squid.Core.Services.Tentacle;

public interface IScriptService
{
    ScriptTicket StartScript(StartScriptCommand command);
    
    ScriptStatusResponse GetStatus(ScriptStatusRequest request);
    
    ScriptStatusResponse CompleteScript(CompleteScriptCommand command);
    
    ScriptStatusResponse CancelScript(CancelScriptCommand command);
}