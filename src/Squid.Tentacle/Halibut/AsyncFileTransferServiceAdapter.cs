using Halibut;
using Squid.Message.Contracts.Tentacle;

namespace Squid.Tentacle.Halibut;

/// <summary>
/// P1-Phase9b.3 — adapts <see cref="IFileTransferService"/> (sync wire
/// contract) to <see cref="IFileTransferServiceAsync"/> (server-facing
/// async surface). Halibut routes incoming RPCs through the async surface
/// so a slow upload can't block a Halibut worker thread.
///
/// <para>The current sync implementation does its own internal async I/O
/// (DataStream's <c>SaveToAsync</c> internally awaits), so this adapter is
/// a thin pass-through. Future agent-side genuinely-async impls would
/// implement <see cref="IFileTransferServiceAsync"/> directly and skip
/// this adapter.</para>
/// </summary>
public sealed class AsyncFileTransferServiceAdapter : IFileTransferServiceAsync
{
    private readonly IFileTransferService _inner;

    public AsyncFileTransferServiceAdapter(IFileTransferService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<UploadResult> UploadFileAsync(string remotePath, DataStream upload, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_inner.UploadFile(remotePath, upload));
    }

    public Task<DataStream> DownloadFileAsync(string remotePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_inner.DownloadFile(remotePath));
    }
}
