using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Reads a whole Octopus space (its catalog, each project's configuration, releases and deployments, then whatever they
/// reference that was not staged) one time-bounded chunk per call. Each chunk advances
/// <see cref="OctopusHistoryFetchContext.Cursor"/>; the chunk that reaches the end builds the session's bundle.
/// </summary>
public interface IOctopusRestHistoryFetcher : IScopedDependency
{
    Task<OctopusServerProbeDto> ProbeAsync(string serverUrl, string apiKey, CancellationToken ct = default);

    Task<OctopusHistoryFetchProgressDto> FetchChunkAsync(OctopusHistoryFetchContext context, CancellationToken ct = default);
}
