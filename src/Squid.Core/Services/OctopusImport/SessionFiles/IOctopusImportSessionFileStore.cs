using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SessionFiles;

/// <summary>
/// The files of an import session, kept in the database so every Squid replica reads what any of them wrote and a restart loses nothing.
/// Paths are relative (<see cref="OctopusImportSessionFilePaths"/>) and compared ordinally. A write replaces the file at its path in one
/// transaction and is refused once the session's upload was cleaned up; a read sees a whole file, the old one or the new one, never a mix.
/// </summary>
public interface IOctopusImportSessionFileStore : IScopedDependency
{
    /// <summary>Stores <paramref name="content"/> at <paramref name="path"/>, replacing what was there. Throws <c>OctopusImportHistoryStagingLostException</c> once the session's files were purged.</summary>
    Task<OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string path, Stream content, OctopusImportSessionFileEncoding encoding, CancellationToken ct = default);

    /// <summary>The stored file's header; null when none is stored at the path.</summary>
    Task<OctopusImportSessionFileHeader> GetHeaderAsync(Guid sessionId, string path, CancellationToken ct = default);

    /// <summary>One small file whole; null when none is stored at the path.</summary>
    Task<byte[]> ReadAsync(Guid sessionId, string path, CancellationToken ct = default);

    /// <summary>Streams the file into <paramref name="destination"/> one chunk at a time, verifying its hash; returns its header, or null when none is stored.</summary>
    Task<OctopusImportSessionFileHeader> CopyToAsync(Guid sessionId, string path, Stream destination, CancellationToken ct = default);

    /// <summary>Every file under <paramref name="prefix"/>, whole, in ordinal path order, read as a stream of files.</summary>
    IAsyncEnumerable<OctopusImportSessionFileContent> ReadAllAsync(Guid sessionId, string prefix, CancellationToken ct = default);

    /// <summary>The headers of every file under <paramref name="prefix"/>, in ordinal path order.</summary>
    Task<IReadOnlyList<OctopusImportSessionFileHeader>> ListAsync(Guid sessionId, string prefix, CancellationToken ct = default);

    Task<OctopusImportSessionFileTotals> TotalsAsync(Guid sessionId, string prefix, CancellationToken ct = default);

    /// <summary>
    /// Deletes every file of a closed session (Succeeded, Failed or Expired, still holding <paramref name="temporaryUploadPath"/>) together
    /// with its file key and export password, and marks its upload cleaned, in one transaction. False when the session is not such a one.
    /// </summary>
    Task<bool> PurgeAndMarkCleanedAsync(Guid sessionId, string temporaryUploadPath, DateTimeOffset cleanedAt, CancellationToken ct = default);

    /// <summary>The sessions among <paramref name="sessionIds"/> whose files were purged.</summary>
    Task<IReadOnlySet<Guid>> PurgedAmongAsync(IReadOnlyCollection<Guid> sessionIds, CancellationToken ct = default);
}
