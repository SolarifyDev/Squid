using Squid.Core.Persistence.Entities.Deployments;

namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

/// <summary>
/// Per-feed-type strategy that lists raw versions discoverable from upstream.
///
/// <para><b>No <c>take</c> on the contract</b>: previous design passed a take limit
/// down into each strategy, but that caused a real production bug — the truncation
/// happened BEFORE <see cref="PackageVersionFilter"/> sorted by semver, so a
/// freshly pushed <c>1.1.0</c> was hidden behind 30 lex-sorted <c>1.0.x-N</c>
/// tags (Docker registries return tags in lexicographic order, where
/// <c>1.0.3-8</c> sorts before <c>1.1.0</c> because <c>0 &lt; 1</c> at the third
/// character). Strategies now return ALL versions discoverable via upstream
/// pagination, and <see cref="PackageVersionFilter.Apply"/> is the single point
/// of filter+sort+take.</para>
///
/// <para><b>Sanity cap via <see cref="PackageVersionEnumerationCap"/></b>: each
/// implementation MUST enforce the cap (default 5000) to prevent OOM on
/// pathological feeds. Operators with legitimately larger feeds override via
/// <c>SQUID_PACKAGE_VERSION_MAX_ENUMERATE</c>.</para>
/// </summary>
public interface IPackageVersionStrategy : IScopedDependency
{
    bool CanHandle(string feedType);

    /// <summary>
    /// Returns ALL versions for the given package as they appear upstream, up to
    /// the enumeration sanity cap. Order is upstream-native (lexicographic for
    /// Docker, date-descending for GitHub, file-order for Helm). The caller —
    /// <c>ExternalFeedPackageVersionService</c> via <see cref="PackageVersionFilter.Apply"/>
    /// — is responsible for filter, semver sort, and take.
    /// </summary>
    Task<List<string>> ListVersionsAsync(ExternalFeed feed, string packageId, CancellationToken ct);
}
