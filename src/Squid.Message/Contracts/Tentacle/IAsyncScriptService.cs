namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// Async proxy interface for the client side (HalibutRuntime.CreateAsyncClient).
/// Methods must NOT include CancellationToken — Halibut routes calls by matching async methods
/// to their sync counterparts in IScriptService, and does not strip CancellationToken.
/// </summary>
public interface IAsyncScriptService
{
    Task<ScriptTicket> StartScriptAsync(StartScriptCommand command);

    Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request);

    Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command);

    Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command);
}
