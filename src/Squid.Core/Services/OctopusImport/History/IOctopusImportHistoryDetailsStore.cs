using Squid.Core.Services.OctopusImport.Octopus;

namespace Squid.Core.Services.OctopusImport.History;

/// <summary>
/// Task history details (activity tree, logs, interruptions, events, artifacts) kept beside the session's
/// upload, one gzip file per task, so they never sit in the resource graph. Confirmation reads them one
/// history unit at a time; the session's secure delete wipes the whole directory.
/// </summary>
public interface IOctopusImportHistoryDetailsStore : IScopedDependency
{
    Task WriteAsync(string sessionDirectory, string taskId, OctopusTaskHistoryDetails details, CancellationToken ct = default);

    /// <summary>The stored details of the task, or null when none were stored.</summary>
    Task<OctopusTaskHistoryDetails> ReadAsync(string sessionDirectory, string taskId, CancellationToken ct = default);
}
