using Squid.Core.Commands.Tentacle;
using Squid.Core.Models.Tentacle;
using Squid.Core.Requests.Tentacle;

namespace Squid.Core.Services.Tentacle;

public interface IAsyncScriptService
{
    Task<ScriptTicket> StartScriptAsync(StartScriptCommand command);
    
    Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request);
    
    Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command);
}