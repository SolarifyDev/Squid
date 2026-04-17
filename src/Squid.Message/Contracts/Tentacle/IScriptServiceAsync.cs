namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// Server-side async interface for DelegateServiceFactory.Register.
/// Halibut's VerifyAsyncSurfaceAreaFollowsConventions requires CancellationToken
/// as the last parameter of each async method.
/// </summary>
public interface IScriptServiceAsync
{
    Task<ScriptStatusResponse> StartScriptAsync(StartScriptCommand command, CancellationToken ct);

    Task<ScriptStatusResponse> GetStatusAsync(ScriptStatusRequest request, CancellationToken ct);

    Task<ScriptStatusResponse> CompleteScriptAsync(CompleteScriptCommand command, CancellationToken ct);

    Task<ScriptStatusResponse> CancelScriptAsync(CancelScriptCommand command, CancellationToken ct);
}
