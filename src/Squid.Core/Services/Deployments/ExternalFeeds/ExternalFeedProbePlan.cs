using System.Net;

namespace Squid.Core.Services.Deployments.ExternalFeeds;

public sealed class ExternalFeedProbePlan
{
    private static readonly HashSet<HttpStatusCode> DefaultReachableStatuses =
    [
        HttpStatusCode.OK,
        HttpStatusCode.Unauthorized,
        HttpStatusCode.Forbidden
    ];

    public ExternalFeedProbePlan(IEnumerable<Uri> probeUris, Func<HttpStatusCode, bool> isReachable = null)
    {
        ProbeUris = (probeUris ?? Enumerable.Empty<Uri>())
            .Where(uri => uri != null)
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IsReachable = isReachable ?? IsDefaultReachable;
    }

    public IReadOnlyList<Uri> ProbeUris { get; }

    public Func<HttpStatusCode, bool> IsReachable { get; }

    public static ExternalFeedProbePlan Single(Uri probeUri, Func<HttpStatusCode, bool> isReachable = null) =>
        new([probeUri], isReachable);

    public static bool IsDefaultReachable(HttpStatusCode status) =>
        DefaultReachableStatuses.Contains(status);
}
