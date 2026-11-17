using System.Security.Cryptography;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport.Exceptions;
using Squid.Core.Services.OctopusImport.SessionFiles;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport;

public sealed record OctopusImportTemporaryUpload(string Path, long SizeBytes, string Sha256 = null);

public sealed record OctopusImportTemporaryUploadDeleteResult(bool Deleted, int FilesDeleted, int DirectoriesDeleted);

public interface IOctopusImportTemporaryUploadStore : IScopedDependency
{
    Task<OctopusImportTemporaryUpload> SaveAsync(Guid sessionId, string sourceFileName, Stream content, CancellationToken ct = default);

    Task<OctopusImportTemporaryUploadDeleteResult> SecureDeleteAsync(Guid sessionId, string temporaryUploadPath, CancellationToken ct = default);

    /// <summary>Securely deletes this server's whole directory of the session (every cached file).</summary>
    Task<OctopusImportTemporaryUploadDeleteResult> SecureDeleteSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>This server's directory of the session's cached files; secure delete wipes all of it.</summary>
    string GetSessionDirectory(Guid sessionId);

    /// <summary>Where this server keeps its copy of a session file registered as <paramref name="temporaryUploadPath"/> (on whichever server).</summary>
    string GetLocalPath(Guid sessionId, string temporaryUploadPath);

    /// <summary>
    /// A local path holding the session's source file (its export upload, or the bundle of a server fetch) exactly as stored with the
    /// session: this server's copy when it is current, otherwise one written now from the database. Null when the session has none.
    /// </summary>
    Task<string> MaterializeAsync(OctopusImportSession session, CancellationToken ct = default);
}

/// <summary>
/// The session's source file is stored with the session (<see cref="IOctopusImportSessionFileStore"/>), the source of truth any replica
/// reads; this server's disk only caches it under its own <c>RootPath</c>, checked against the stored hash before use. A session uploaded
/// before files were stored with the session has no stored copy: its registered path is used when this server has it.
/// </summary>
public sealed class OctopusImportTemporaryUploadStore(IOctopusImportTemporaryUploadSettings settings, IOctopusImportSessionFileStore files) : IOctopusImportTemporaryUploadStore
{
    /// <summary>Beside each cached file: the SHA-256 of its bytes, so a current copy is recognised without hashing it again.</summary>
    public const string HashSuffix = ".sha256";

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

        try
        {
            return await WriteAndStoreAsync(sessionId, sourceFileName, content, ct).ConfigureAwait(false);
        }
        catch
        {
            await DiscardSessionDirectoryAsync(sessionId).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<OctopusImportTemporaryUpload> WriteAndStoreAsync(Guid sessionId, string sourceFileName, Stream content, CancellationToken ct)
    {
        var directory = GetSessionDirectory(sessionId);
        Directory.CreateDirectory(directory);

        var fileName = SanitizeFileName(sourceFileName);
        var destinationPath = Path.Combine(directory, fileName);

        await using var output = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = SHA256.Create();
        var buffer = new byte[81920];

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read == 0)
                break;

            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            hash.TransformBlock(buffer, 0, read, buffer, 0);
        }

        hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        await output.FlushAsync(ct).ConfigureAwait(false);
        await output.DisposeAsync().ConfigureAwait(false);

        var sha256 = Convert.ToHexString(hash.Hash).ToLowerInvariant();

        await StoreAsync(sessionId, destinationPath, sha256, ct).ConfigureAwait(false);

        return new OctopusImportTemporaryUpload(destinationPath, new FileInfo(destinationPath).Length, sha256);
    }

    // An upload that could not be stored with the session is never registered, so no cleanup would ever find this server's copy (an export
    // with secrets): it goes now. Uncancellable, and a failure here never masks why the upload failed.
    private async Task DiscardSessionDirectoryAsync(Guid sessionId)
    {
        try
        {
            await SecureDeleteSessionDirectoryAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning("Octopus import session {SessionId} could not remove its unstored upload from this server ({ExceptionType})", sessionId, ex.GetType().Name);
        }
    }

    // The written file is stored with the session, read back from disk, so the upload is never held in memory whole.
    private async Task StoreAsync(Guid sessionId, string localPath, string sha256, CancellationToken ct)
    {
        await using (var written = OpenRead(localPath))
            await files.WriteAsync(sessionId, Path.GetFileName(localPath), written, OctopusImportSessionFileEncoding.Identity, ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(localPath + HashSuffix, sha256, ct).ConfigureAwait(false);
    }

    public string GetLocalPath(Guid sessionId, string temporaryUploadPath) => Path.Combine(GetSessionDirectory(sessionId), Path.GetFileName(temporaryUploadPath));

    public async Task<string> MaterializeAsync(OctopusImportSession session, CancellationToken ct = default)
    {
        var name = OctopusImportSessionFilePaths.SourceOf(session);

        if (name == null) return null;

        var header = await files.GetHeaderAsync(session.SessionId, name, ct).ConfigureAwait(false);

        if (header == null) return LegacyLocalPathOrNull(session.TemporaryUploadPath);

        var localPath = GetLocalPath(session.SessionId, name);

        if (await IsCurrentAsync(localPath, header, ct).ConfigureAwait(false)) return localPath;

        return await RehydrateAsync(session.SessionId, localPath, ct).ConfigureAwait(false);
    }

    // Uploaded before session files were stored in the database: only the server that took the upload has it.
    private static string LegacyLocalPathOrNull(string temporaryUploadPath) => File.Exists(temporaryUploadPath) ? temporaryUploadPath : null;

    // The hash beside the copy names what the copy holds; a copy without one (a bundle this server just built) is hashed once.
    private static async Task<bool> IsCurrentAsync(string localPath, OctopusImportSessionFileHeader header, CancellationToken ct)
    {
        if (!File.Exists(localPath) || new FileInfo(localPath).Length != header.Length) return false;

        var recorded = File.Exists(localPath + HashSuffix) ? (await File.ReadAllTextAsync(localPath + HashSuffix, ct).ConfigureAwait(false)).Trim() : null;

        if (recorded != null) return string.Equals(recorded, header.Sha256, StringComparison.OrdinalIgnoreCase);

        var actual = await HashFileAsync(localPath, ct).ConfigureAwait(false);

        if (!string.Equals(actual, header.Sha256, StringComparison.OrdinalIgnoreCase)) return false;

        await File.WriteAllTextAsync(localPath + HashSuffix, actual, ct).ConfigureAwait(false);

        return true;
    }

    // Written beside the target and moved over it only once complete and verified; a stored file that cannot be read back intact is no source.
    private async Task<string> RehydrateAsync(Guid sessionId, string localPath, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        OctopusImportSessionFileHeader header = null;

        try
        {
            await OctopusImportAtomicFile.WriteAsync(localPath, async (stream, token) => header = await files.CopyToAsync(sessionId, Path.GetFileName(localPath), stream, token).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        catch (OctopusImportSessionFileUnreadableException)
        {
            Log.Warning("Octopus import session {SessionId} has a stored source file that cannot be read back intact; it is treated as missing", sessionId);
            return null;
        }

        return await RecordRehydratedAsync(localPath, header, ct).ConfigureAwait(false);
    }

    private static async Task<string> RecordRehydratedAsync(string localPath, OctopusImportSessionFileHeader header, CancellationToken ct)
    {
        if (header == null)
        {
            File.Delete(localPath);
            return null;
        }

        await File.WriteAllTextAsync(localPath + HashSuffix, header.Sha256, ct).ConfigureAwait(false);

        return localPath;
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var file = OpenRead(path);

        return Convert.ToHexString(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

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

        var uploadDeleted = 0;

        if (File.Exists(fullUploadPath))
        {
            await SecureDeleteFileAsync(fullUploadPath, ct).ConfigureAwait(false);
            uploadDeleted++;
        }

        var directory = await SecureDeleteSessionDirectoryAsync(sessionId, ct).ConfigureAwait(false);

        return directory with { FilesDeleted = directory.FilesDeleted + uploadDeleted };
    }

    public async Task<OctopusImportTemporaryUploadDeleteResult> SecureDeleteSessionDirectoryAsync(Guid sessionId, CancellationToken ct = default)
    {
        var sessionDirectory = GetSessionDirectory(sessionId);
        var filesDeleted = 0;
        var directoriesDeleted = 0;

        if (!Directory.Exists(sessionDirectory)) return new OctopusImportTemporaryUploadDeleteResult(true, 0, 0);

        foreach (var file in Directory.EnumerateFiles(sessionDirectory, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            await SecureDeleteFileAsync(file, ct).ConfigureAwait(false);
            filesDeleted++;
        }

        foreach (var directory in Directory.EnumerateDirectories(sessionDirectory, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            ct.ThrowIfCancellationRequested();

            if (!IsDirectoryEmpty(directory)) continue;

            Directory.Delete(directory);
            directoriesDeleted++;
        }

        if (IsDirectoryEmpty(sessionDirectory))
        {
            Directory.Delete(sessionDirectory);
            directoriesDeleted++;
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

    public string GetSessionDirectory(Guid sessionId)
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

        if (string.IsNullOrWhiteSpace(fileName)) return "upload.bin";

        // The history bundle name is reserved for the REST fetch; an upload must never be taken for (or overwritten by) it.
        return OctopusImportSourceReader.IsHistoryBundle(fileName) ? $"upload-{fileName}" : fileName;
    }
}
