using Halibut;

namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// P1-Phase9b.3 — server-side (agent-side) async surface for
/// <see cref="IFileTransferService"/>. Used by the agent's
/// <c>DelegateServiceFactory.Register&lt;IFileTransferService, IFileTransferServiceAsync&gt;</c>
/// — Halibut routes inbound RPCs through this interface, allowing the agent
/// to await the actual disk I/O without blocking a Halibut worker thread.
///
/// <para>Methods MAY include <see cref="CancellationToken"/> — the agent-
/// side adapter receives the CT for each call. Pattern matches
/// <c>IScriptServiceAsync</c> + <c>ICapabilitiesServiceAsync</c>.</para>
/// </summary>
public interface IFileTransferServiceAsync
{
    Task<UploadResult> UploadFileAsync(string remotePath, DataStream upload, CancellationToken ct);

    Task<DataStream> DownloadFileAsync(string remotePath, CancellationToken ct);
}
