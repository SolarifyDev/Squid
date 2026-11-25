using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Packages.BuiltIn;

public interface IBuiltInFeedLocator : IScopedDependency
{
    /// <summary>The space's built-in feed. Throws <c>BuiltInFeedNotFoundException</c> when the space has none.</summary>
    Task<ExternalFeed> GetAsync(int spaceId, CancellationToken ct);
}
