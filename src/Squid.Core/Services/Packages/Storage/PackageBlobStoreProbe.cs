using System.Text;
using Squid.Message.Models.PackageStorage;

namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// The write, read back, delete check every provider's test runs against a store. Provider-neutral: it talks to <see cref="IPackageBlobStore"/> only.
/// </summary>
public static class PackageBlobStoreProbe
{
    public const string KeyPrefix = ".squid-probe/";

    public static async Task<PackageBlobStoreTestResult> RunAsync(IPackageBlobStore store, CancellationToken ct)
    {
        var key = $"{KeyPrefix}{Guid.NewGuid():N}";
        var payload = Encoding.UTF8.GetBytes($"squid probe {Guid.NewGuid():N}");

        try
        {
            await WriteAsync(store, key, payload, ct).ConfigureAwait(false);

            var readBack = await ReadAsync(store, key, ct).ConfigureAwait(false);

            if (!readBack.AsSpan().SequenceEqual(payload))
                return Failed("The probe file was written, but reading it back returned different content.");

            return Passed("Wrote, read back and deleted a probe file.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed($"The store did not work: {ex.Message}");
        }
        finally
        {
            await DeleteQuietlyAsync(store, key).ConfigureAwait(false);
        }
    }

    private static async Task WriteAsync(IPackageBlobStore store, string key, byte[] payload, CancellationToken ct)
    {
        using var content = new MemoryStream(payload);

        await store.PutAsync(key, content, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadAsync(IPackageBlobStore store, string key, CancellationToken ct)
    {
        await using var stream = await store.OpenReadAsync(key, ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);

        return buffer.ToArray();
    }

    private static async Task DeleteQuietlyAsync(IPackageBlobStore store, string key)
    {
        try
        {
            await store.DeleteAsync(key, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The probe has already reported what it found; a leftover probe file is harmless.
        }
    }

    private static PackageBlobStoreTestResult Passed(string message) => new() { Success = true, Message = message };

    private static PackageBlobStoreTestResult Failed(string message) => new() { Success = false, Message = message };
}
