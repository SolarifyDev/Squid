namespace Squid.Core.Services.Packages.Versioning;

public interface IPackageVersionCounterStore : IScopedDependency
{
    /// <summary>
    /// Takes the next number of the counter for (<paramref name="feedId"/>, <paramref name="packageId"/>, <paramref name="scope"/>), starting at 1. The number is committed
    /// before this returns and is never given out again, so two callers never share one; a caller that later fails leaves a gap, not a duplicate.
    /// </summary>
    Task<long> NextAsync(int feedId, string packageId, string scope, CancellationToken ct);
}
