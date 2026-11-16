using Squid.Message.Models.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Rest;

/// <summary>Lists an Octopus space's projects and project groups, so the user chooses what to import before anything is read.</summary>
public interface IOctopusRestProjectLister : IScopedDependency
{
    /// <summary>The space's projects and project groups only — no per-project request.</summary>
    Task<OctopusRestProjectListing> ListProjectsAsync(OctopusServerConnection connection, CancellationToken ct = default);
}
