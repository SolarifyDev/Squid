using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>
/// Task history details (activity tree, logs, interruptions, events, artifacts) stored with the session's files, one gzip document per
/// task, so they never sit in the resource graph and any replica can read them. Confirmation reads them one history unit at a time; the
/// session's cleanup deletes them with every other file of the session.
/// </summary>
public interface IOctopusImportHistoryDetailsStore : IScopedDependency
{
    /// <returns>The write: <see cref="OctopusImportSessionFileWrite.Growth"/> is how many bytes the stored details grew by.</returns>
    Task<OctopusImportSessionFileWrite> WriteAsync(Guid sessionId, string taskId, OctopusTaskHistoryDetails details, CancellationToken ct = default);

    /// <summary>The stored details of the task, or null when none were stored.</summary>
    Task<OctopusTaskHistoryDetails> ReadAsync(Guid sessionId, string taskId, CancellationToken ct = default);

    /// <summary>How many tasks have details stored.</summary>
    Task<int> CountAsync(Guid sessionId, CancellationToken ct = default);
}
