using System.Runtime.CompilerServices;
using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>Keeps each blob as a file under <paramref name="root"/>, at the path its key spells.</summary>
public sealed class FileSystemPackageBlobStore(string root) : IPackageBlobStore
{
    public const string StoreKind = "FileSystem";

    public string Kind => StoreKind;

    public string Root { get; } = root;

    /// <summary>The content is written to a staging file first and moved over the target only once it is complete, so a failure leaves any earlier blob as it was.</summary>
    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        var target = FileSystemBlobKey.ToFullPath(Root, key);
        var staging = NewStagingPath();

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);

        try
        {
            await WriteStagingFileAsync(staging, content, ct).ConfigureAwait(false);

            File.Move(staging, target, overwrite: true);
        }
        catch
        {
            DiscardStaging(staging);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = FileSystemBlobKey.ToFullPath(Root, key);

        try
        {
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PackageBlobNotFoundException(key);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(File.Exists(FileSystemBlobKey.ToFullPath(Root, key)));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        // File.Delete does nothing for a missing file, but throws when a parent folder is missing too.
        DeleteQuietly(FileSystemBlobKey.ToFullPath(Root, key));

        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<string> ListKeysAsync(string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        FileSystemBlobKey.EnsurePrefixSafe(prefix);

        await Task.Yield();

        foreach (var key in EnumerateKeys(prefix ?? string.Empty))
        {
            ct.ThrowIfCancellationRequested();

            yield return key;
        }
    }

    private IEnumerable<string> EnumerateKeys(string prefix)
    {
        var folder = Path.Combine(Root, prefix.Contains('/') ? prefix[..prefix.LastIndexOf('/')] : string.Empty);

        if (!Directory.Exists(folder)) return [];

        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(key => !FileSystemBlobKey.IsStaging(key) && key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private string NewStagingPath() => Path.Combine(Root, FileSystemBlobKey.StagingFolderName, Guid.NewGuid().ToString("N"));

    private static async Task WriteStagingFileAsync(string path, Stream content, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        await content.CopyToAsync(file, ct).ConfigureAwait(false);
        await file.FlushAsync(ct).ConfigureAwait(false);

        file.Flush(flushToDisk: true);
    }

    // Cleaning up must never replace the failure that made it necessary.
    private static void DiscardStaging(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
