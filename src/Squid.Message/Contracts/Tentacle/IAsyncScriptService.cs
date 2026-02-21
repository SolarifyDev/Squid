namespace Squid.Message.Contracts.Tentacle;

public interface IAsyncScriptService
{
    Task<ScriptTicket> StartScriptAsync(StartScriptCommand command);

    Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request);

    Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command);

    Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command);
}
