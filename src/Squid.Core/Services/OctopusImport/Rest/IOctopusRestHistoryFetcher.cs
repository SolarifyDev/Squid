using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Reads a whole Octopus space (its catalog, each project's configuration, releases and deployments, then whatever they
/// reference that was not staged) one time-bounded chunk per call. Each chunk advances
/// <see cref="OctopusHistoryFetchContext.Cursor"/>; the chunk that reaches the end builds the session's bundle.
/// </summary>
public interface IOctopusRestHistoryFetcher : IScopedDependency
{
    /// <summary>Reaches the server, lists its spaces and reports what the key can read in the named (or default) space.</summary>
    Task<OctopusServerProbeDto> ProbeAsync(OctopusServerConnection connection, CancellationToken ct = default);

    /// <summary>Checks the key can read the whole space and returns a fetch cursor pinned to the installation, space, user and key; throws when it cannot.</summary>
    Task<OctopusImportHistoryFetchDto> AuthorizeAsync(OctopusServerConnection connection, CancellationToken ct = default);

    Task<OctopusHistoryFetchProgressDto> FetchChunkAsync(OctopusHistoryFetchContext context, CancellationToken ct = default);
}
