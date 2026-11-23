namespace Squid.Message.Contracts.Tentacle.V2;

/// <summary>
/// Server-side async interface for <c>DelegateServiceFactory.Register&lt;IScriptServiceV2, IScriptServiceV2Async&gt;</c>.
/// Like <see cref="IScriptServiceAsync"/>, Halibut requires CancellationToken as the last parameter.
/// </summary>
public interface IScriptServiceV2Async
{
    Task<ScriptStatusResponseV2> StartScriptAsync(StartScriptCommandV2 command, CancellationToken ct);

    Task<ScriptStatusResponseV2> GetStatusAsync(ScriptStatusRequestV2 request, CancellationToken ct);

    Task<ScriptStatusResponseV2> CancelScriptAsync(CancelScriptCommandV2 command, CancellationToken ct);

    Task<CompleteScriptResponseV2> CompleteScriptAsync(CompleteScriptCommandV2 command, CancellationToken ct);
}
