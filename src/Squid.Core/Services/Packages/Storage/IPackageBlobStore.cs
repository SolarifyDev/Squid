namespace Squid.Core.Services.Packages.Storage;

/// <summary>
/// Keeps named blobs of bytes. It knows nothing about packages, versions or feeds: callers choose the keys. A key is relative and '/'-separated
/// (for example <c>acme/web/1.0.0.zip</c>); each store rejects keys it cannot keep safely with <c>InvalidPackageBlobKeyException</c>.
/// </summary>
public interface IPackageBlobStore
{
    string Kind { get; }

    /// <summary>Stores the content under the key, replacing any blob already there. A reader never sees a half-written blob.</summary>
    Task PutAsync(string key, Stream content, CancellationToken ct);

    /// <summary>Opens the blob for reading; the caller disposes the stream. Throws <c>PackageBlobNotFoundException</c> when there is none.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct);

    Task<bool> ExistsAsync(string key, CancellationToken ct);

    /// <summary>Removes the blob; nothing happens when it is not there.</summary>
    Task DeleteAsync(string key, CancellationToken ct);

    /// <summary>The keys that start with the prefix (every key for an empty prefix), in no particular order.</summary>
    IAsyncEnumerable<string> ListKeysAsync(string prefix, CancellationToken ct);
}
