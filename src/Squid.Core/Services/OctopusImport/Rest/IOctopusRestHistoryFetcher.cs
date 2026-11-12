using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>
/// Reads deployment history from an Octopus server, one time-bounded chunk per call. Each chunk advances
/// <see cref="OctopusHistoryFetchContext.Cursor"/>; the chunk that reaches the end builds the history bundle.
/// </summary>
public interface IOctopusRestHistoryFetcher : IScopedDependency
{
    Task<OctopusServerProbeDto> ProbeAsync(string serverUrl, string apiKey, CancellationToken ct = default);

    Task<OctopusHistoryFetchProgressDto> FetchChunkAsync(OctopusHistoryFetchContext context, CancellationToken ct = default);
}
