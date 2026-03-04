using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public interface IExternalFeedProbeRule<out TResult> : IScopedDependency
{
    int Order { get; }

    bool Matches(ExternalFeed feed);

    TResult Resolve(ExternalFeed feed, Uri normalizedBaseUri);
}
