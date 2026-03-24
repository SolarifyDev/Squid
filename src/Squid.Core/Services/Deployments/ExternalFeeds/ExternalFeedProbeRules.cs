using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public abstract class ExternalFeedProbeRuleBase : IExternalFeedProbeRule<ExternalFeedProbePlan>
{
    public abstract int Order { get; }

    public abstract bool Matches(ExternalFeed feed);

    public abstract ExternalFeedProbePlan Resolve(ExternalFeed feed, Uri normalizedBaseUri);

    protected static bool FeedTypeContains(ExternalFeed feed, string feedTypeKeyword) =>
        !string.IsNullOrWhiteSpace(feed?.FeedType) &&
        feed.FeedType.Contains(feedTypeKeyword, StringComparison.OrdinalIgnoreCase);

    protected static bool FeedTypeContainsAny(ExternalFeed feed, params string[] feedTypeKeywords) =>
        feedTypeKeywords is { Length: > 0 } && feedTypeKeywords.Any(keyword => FeedTypeContains(feed, keyword));
}

public sealed class ExternalFeedDockerProbeRule : ExternalFeedProbeRuleBase
{
    private static readonly string[] ContainerRegistryTypeKeywords =
    [
        "Docker",
        "Container Registry",
        "Elastic Container Registry",
        "OCI Registry",
        "ECR",
        "ACR",
        "GCR"
    ];

    public override int Order => 100;

    public override bool Matches(ExternalFeed feed) => FeedTypeContainsAny(feed, ContainerRegistryTypeKeywords);

    public override ExternalFeedProbePlan Resolve(ExternalFeed feed, Uri normalizedBaseUri)
    {
        var apiVersion = NormalizeDockerApiVersion(ExternalFeedProperties.GetApiVersion(feed));
        if (!string.IsNullOrWhiteSpace(apiVersion))
            return ExternalFeedProbePlan.Single(ExternalFeedProbeUri.EnsureEndsWithPathSegment(normalizedBaseUri, apiVersion));

        if (ExternalFeedProbeUri.EndsWithPathSegment(normalizedBaseUri, "v1") ||
            ExternalFeedProbeUri.EndsWithPathSegment(normalizedBaseUri, "v2"))
            return ExternalFeedProbePlan.Single(normalizedBaseUri);

        // Trailing slash is critical: registries like ACR 301-redirect /v2 → /v2/,
        // and HttpClient strips the Authorization header on redirect.
        return new ExternalFeedProbePlan(
        [
            ExternalFeedProbeUri.EnsureEndsWithPathSegment(normalizedBaseUri, "v2/"),
            ExternalFeedProbeUri.EnsureEndsWithPathSegment(normalizedBaseUri, "v1/")
        ]);
    }

    private static string NormalizeDockerApiVersion(string apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
            return null;

        return apiVersion.Trim().ToLowerInvariant() switch
        {
            "1" or "v1" => "v1/",
            "2" or "v2" => "v2/",
            _ => null
        };
    }
}

public sealed class ExternalFeedGitHubProbeRule : ExternalFeedProbeRuleBase
{
    public override int Order => 150;

    public override bool Matches(ExternalFeed feed) => FeedTypeContains(feed, "GitHub");

    public override ExternalFeedProbePlan Resolve(ExternalFeed feed, Uri normalizedBaseUri) =>
        ExternalFeedProbePlan.Single(normalizedBaseUri);
}

public sealed class ExternalFeedHelmProbeRule : ExternalFeedProbeRuleBase
{
    public override int Order => 200;

    public override bool Matches(ExternalFeed feed) =>
        FeedTypeContains(feed, "Helm");

    public override ExternalFeedProbePlan Resolve(ExternalFeed feed, Uri normalizedBaseUri) =>
        ExternalFeedProbePlan.Single(ExternalFeedProbeUri.EnsureEndsWithPathSegment(normalizedBaseUri, "index.yaml"));
}

public sealed class ExternalFeedDefaultProbeRule : ExternalFeedProbeRuleBase
{
    public static ExternalFeedDefaultProbeRule Instance { get; } = new();

    public override int Order => int.MaxValue;

    public override bool Matches(ExternalFeed feed) => true;

    public override ExternalFeedProbePlan Resolve(ExternalFeed feed, Uri normalizedBaseUri) =>
        ExternalFeedProbePlan.Single(normalizedBaseUri);
}
