using System.Runtime.CompilerServices;
using Squid.Core.Services.Packages.Storage.Exceptions;

namespace Squid.Core.Services.Packages.Storage.Providers.FileSystem;

/// <summary>Keeps each blob as a file under <paramref name="root"/>, at the path its key spells.</summary>
public sealed class FileSystemPackageBlobStore(string root, Func<DateTime> utcNow = null) : IPackageBlobStore
{
    public const string StoreKind = "FileSystem";

    /// <summary>A staging file this old was left by a write that never finished (a crash, a kill): no live write takes this long.</summary>
    public static readonly TimeSpan StagingMaxAge = TimeSpan.FromHours(24);

    /// <summary>The staging folder is swept at most this often per store instance, on a write.</summary>
    public static readonly TimeSpan StagingSweepInterval = TimeSpan.FromHours(1);

    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private readonly object _sweepLock = new();
    private DateTime _lastSweepUtc = DateTime.MinValue;

    public string Kind => StoreKind;

    public string Root { get; } = root;

    /// <summary>The content is written to a staging file first and moved over the target only once it is complete, so a failure leaves any earlier blob as it was.</summary>
    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        var target = ResolveForAccess(key);
        var staging = NewStagingPath();

        SweepStagingIfDue();

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        Directory.CreateDirectory(Path.GetDirectoryName(staging)!);

        // The staging folder is the store's own, but it is just a folder: a link planted in its place must not receive the bytes.
        FileSystemLinks.EnsureNoLinks(Root, staging, key);

        try
        {
            await WriteStagingFileAsync(staging, content, ct).ConfigureAwait(false);

            File.Move(staging, target, overwrite: true);

            TryFlushDirectory(Path.GetDirectoryName(target)!);
        }
        catch
        {
            DiscardStaging(staging);
            throw;
        }
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var path = ResolveForAccess(key);

        // A key that names a folder (another key is nested under it) is not a blob.
        if (!File.Exists(path))
            throw new PackageBlobNotFoundException(key);

        try
        {
            return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, useAsync: true));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new PackageBlobNotFoundException(key);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(File.Exists(ResolveForAccess(key)));

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        // File.Delete does nothing for a missing file, but throws when a parent folder is missing too.
        DeleteQuietly(ResolveForAccess(key));

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

    /// <summary>Removes staging files older than <see cref="StagingMaxAge"/>. Best effort: a file that cannot be removed now is tried again at the next sweep.</summary>
    public void SweepStaging()
    {
        var staging = Path.Combine(Root, FileSystemBlobKey.StagingFolderName);

        if (!Directory.Exists(staging)) return;

        var cutoff = _utcNow() - StagingMaxAge;

        foreach (var file in EnumerateQuietly(staging).Where(f => File.GetLastWriteTimeUtc(f) < cutoff))
            DiscardStaging(file);
    }

    /// <summary>Flushes the folder entry to disk so a moved-in file survives a power cut. Not supported everywhere (Windows), so any failure is ignored.</summary>
    public static void TryFlushDirectory(string directory)
    {
        try
        {
            using var handle = File.OpenHandle(directory, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);

            RandomAccess.FlushToDisk(handle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }
    }

    private string ResolveForAccess(string key)
    {
        var path = FileSystemBlobKey.ToFullPath(Root, key);

        FileSystemLinks.EnsureNoLinks(Root, path, key);

        return path;
    }

    private void SweepStagingIfDue()
    {
        lock (_sweepLock)
        {
            if (_utcNow() - _lastSweepUtc < StagingSweepInterval) return;

            _lastSweepUtc = _utcNow();
        }

        SweepStaging();
    }

    private IEnumerable<string> EnumerateKeys(string prefix)
    {
        var folder = Path.Combine(Root, prefix.Contains('/') ? prefix[..prefix.LastIndexOf('/')] : string.Empty);

        if (!Directory.Exists(folder)) return [];

        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };

        return Directory.EnumerateFiles(folder, "*", options)
            .Select(path => Path.GetRelativePath(Root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(key => !FileSystemBlobKey.IsHiddenFromListing(key) && key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateQuietly(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
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
