using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.SourceMap;

public interface IOctopusImportSourceMapDataProvider : IScopedDependency
{
    /// <summary>Every live source-map row of the space. Rows whose destination row no longer exists (or whose type has no known table) are dropped.</summary>
    Task<OctopusImportPriorMappings> LoadAsync(int spaceId, CancellationToken ct = default);

    /// <summary>
    /// Inserts or updates the rows in batches. A row that was <c>Created</c> keeps that outcome when re-recorded against the
    /// same destination, and a null incoming fingerprint keeps the stored one (details are monotonic). Call it only inside the
    /// confirmation transaction, after <see cref="AcquireSpaceLockAsync"/>.
    /// </summary>
    Task UpsertAsync(IReadOnlyList<OctopusImportSourceMapRow> rows, Guid sessionId, CancellationToken ct = default);

    /// <summary>Serialises confirmations of one space for the rest of the CURRENT transaction (released at commit or rollback).</summary>
    Task AcquireSpaceLockAsync(int spaceId, CancellationToken ct = default);
}
