using System.Security.Cryptography;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportTemporaryUpload(string Path, long SizeBytes);

public sealed record OctopusImportTemporaryUploadDeleteResult(bool Deleted, int FilesDeleted, int DirectoriesDeleted);

public interface IOctopusImportTemporaryUploadStore : IScopedDependency
{
    Task<OctopusImportTemporaryUpload> SaveAsync(Guid sessionId, string sourceFileName, Stream content, CancellationToken ct = default);

    Task<OctopusImportTemporaryUploadDeleteResult> SecureDeleteAsync(Guid sessionId, string temporaryUploadPath, CancellationToken ct = default);
}

public sealed class OctopusImportTemporaryUploadStore(
    IOctopusImportTemporaryUploadSettings settings) : IOctopusImportTemporaryUploadStore
{
    public async Task<OctopusImportTemporaryUpload> SaveAsync(
        Guid sessionId,
        string sourceFileName,
        Stream content,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (content == null)
            throw new ArgumentNullException(nameof(content));

        var directory = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);

        var fileName = SanitizeFileName(sourceFileName);
        var destinationPath = Path.Combine(directory, fileName);

        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(output, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);

        return new OctopusImportTemporaryUpload(destinationPath, output.Length);
    }

    public async Task<OctopusImportTemporaryUploadDeleteResult> SecureDeleteAsync(
        Guid sessionId,
        string temporaryUploadPath,
        CancellationToken ct = default)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(temporaryUploadPath))
            return new OctopusImportTemporaryUploadDeleteResult(true, 0, 0);

        var sessionDirectory = GetSessionDirectory(sessionId);
        var fullUploadPath = Path.GetFullPath(temporaryUploadPath);
        EnsurePathIsInsideSessionDirectory(sessionId, fullUploadPath, sessionDirectory);

        var filesDeleted = 0;
        var directoriesDeleted = 0;

        if (File.Exists(fullUploadPath))
        {
            await SecureDeleteFileAsync(fullUploadPath, ct).ConfigureAwait(false);
            filesDeleted++;
        }

        if (Directory.Exists(sessionDirectory))
        {
            foreach (var file in Directory.EnumerateFiles(sessionDirectory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                await SecureDeleteFileAsync(file, ct).ConfigureAwait(false);
                filesDeleted++;
            }

            foreach (var directory in Directory
                         .EnumerateDirectories(sessionDirectory, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                ct.ThrowIfCancellationRequested();
                if (IsDirectoryEmpty(directory))
                {
                    Directory.Delete(directory);
                    directoriesDeleted++;
                }
            }

            if (IsDirectoryEmpty(sessionDirectory))
            {
                Directory.Delete(sessionDirectory);
                directoriesDeleted++;
            }
        }

        return new OctopusImportTemporaryUploadDeleteResult(true, filesDeleted, directoriesDeleted);
    }

    private async Task SecureDeleteFileAsync(string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
            return;

        var length = info.Length;
        if (length > 0)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, settings.SecureDeleteBufferBytes, FileOptions.WriteThrough | FileOptions.Asynchronous);
            var buffer = new byte[settings.SecureDeleteBufferBytes];
            long remaining = length;

            while (remaining > 0)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, remaining);
                RandomNumberGenerator.Fill(buffer.AsSpan(0, count));
                await stream.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                remaining -= count;
            }

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }

        File.Delete(path);
    }

    private string GetSessionDirectory(Guid sessionId)
    {
        var root = Path.GetFullPath(settings.RootPath);
        var directory = Path.Combine(root, sessionId.ToString("N"));

        return Path.GetFullPath(directory);
    }

    private void EnsurePathIsInsideSessionDirectory(Guid sessionId, string fullUploadPath, string sessionDirectory)
    {
        var fullRoot = Path.GetFullPath(settings.RootPath);
        if (!IsPathUnder(fullRoot, sessionDirectory))
            throw new InvalidOperationException("Octopus import temporary upload root is invalid.");

        if (!StringComparer.Ordinal.Equals(Path.GetFileName(sessionDirectory), sessionId.ToString("N")))
            throw new InvalidOperationException("Octopus import temporary upload session directory is invalid.");

        if (!IsPathUnder(sessionDirectory, fullUploadPath))
            throw new InvalidOperationException("Octopus import temporary upload path is outside the expected session directory.");
    }

    private static bool IsPathUnder(string parent, string child)
    {
        var fullParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullChild = Path.GetFullPath(child);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        return fullChild.StartsWith(fullParent, comparison);
    }

    private static bool IsDirectoryEmpty(string directory) => !Directory.EnumerateFileSystemEntries(directory).Any();

    private static string SanitizeFileName(string sourceFileName)
    {
        var fileName = Path.GetFileName(sourceFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "upload.bin";

        foreach (var invalid in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(fileName) ? "upload.bin" : fileName;
    }
}
