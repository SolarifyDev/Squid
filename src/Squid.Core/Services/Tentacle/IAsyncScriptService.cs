using Squid.Core.Commands.Tentacle;

namespace Squid.Core.Services.Tentacle;

public interface IAsyncScriptService
{
    Task<ScriptTicket> StartScriptAsync(StartScriptCommand command);
    
    Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request);
    
    Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command);
    
    Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command);
}