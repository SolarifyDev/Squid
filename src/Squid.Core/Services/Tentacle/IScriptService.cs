using Squid.Core.Commands.Tentacle;
using Squid.Core.Models.Tentacle;
using Squid.Core.Requests.Tentacle;

namespace Squid.Core.Services.Tentacle;

public interface IScriptService
{
    ScriptTicket StartScript(StartScriptCommand command);
    
    ScriptStatusResponse GetStatus(ScriptStatusRequest request);
    
    ScriptStatusResponse CompleteScript(CompleteScriptCommand command);
}