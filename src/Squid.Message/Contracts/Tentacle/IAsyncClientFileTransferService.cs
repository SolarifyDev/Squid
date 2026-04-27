using Halibut;

namespace Squid.Message.Contracts.Tentacle;

/// <summary>
/// P1-Phase9b.3 — client-side async surface for <see cref="IFileTransferService"/>.
/// Used by the server's
/// <c>HalibutRuntime.CreateAsyncClient&lt;IFileTransferService, IAsyncClientFileTransferService&gt;(endpoint)</c>
/// — the server invokes file uploads/downloads via this proxy.
///
/// <para>Methods do NOT include <see cref="CancellationToken"/> — Halibut
/// matches async methods to their sync counterparts in
/// <see cref="IFileTransferService"/> by parameter shape and does not
/// strip CT. Pattern matches <c>IAsyncCapabilitiesService</c> +
/// <c>IAsyncScriptService</c>.</para>
/// </summary>
public interface IAsyncClientFileTransferService
{
    Task<UploadResult> UploadFileAsync(string remotePath, DataStream upload);

    Task<DataStream> DownloadFileAsync(string remotePath);
}
