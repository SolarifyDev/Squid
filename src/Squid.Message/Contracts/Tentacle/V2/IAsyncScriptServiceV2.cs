namespace Squid.Message.Contracts.Tentacle.V2;

/// <summary>
/// Async proxy interface for the client side (<c>HalibutRuntime.CreateAsyncClient&lt;IScriptServiceV2, IAsyncScriptServiceV2&gt;</c>).
/// Like <see cref="IAsyncScriptService"/>, methods must NOT include CancellationToken.
/// </summary>
public interface IAsyncScriptServiceV2
{
    Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command);

    Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request);

    Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command);

    Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command);
}
