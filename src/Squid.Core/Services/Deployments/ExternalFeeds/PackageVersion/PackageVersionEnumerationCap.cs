namespace Squid.Core.Services.Deployments.ExternalFeeds.PackageVersion;

/// <summary>
/// Sanity cap on how many versions a single <see cref="IPackageVersionStrategy"/>
/// will pull from upstream during one <c>ListVersionsAsync</c> call.
///
/// <para><b>Why this exists</b>: strategies now follow pagination links until the
/// source is exhausted (Docker registry Link header, GitHub Releases Link header).
/// A pathological feed (e.g. an internal mirror that mirrors millions of tags from
/// upstream registries) could otherwise OOM the API process by streaming tags
/// indefinitely. The cap is the upstream-fetch ceiling — the per-call <c>take</c>
/// the operator passes in is applied AFTER semver sort by
/// <see cref="PackageVersionFilter.Apply"/>, so the cap doesn't change what the
/// user sees in the dropdown unless their target version is past the cap.</para>
///
/// <para><b>Default 5000</b>: an order of magnitude above any package's typical
/// release history (Docker Hub's nginx has ~3000 tags as the largest practical
/// example), but well below the ~50–100K threshold where memory pressure becomes
/// a concern (each tag string is ~32 bytes; 5000 × 32 = 160 KB per call).</para>
///
/// <para><b>Operator escape hatch</b> (Rule 8): air-gapped operators with private
/// mirrors that legitimately have higher tag counts can override via
/// <c>SQUID_PACKAGE_VERSION_MAX_ENUMERATE</c> env var. Reads on each call so a
/// restart isn't needed to bump the cap mid-incident.</para>
/// </summary>
public static class PackageVersionEnumerationCap
{
    /// <summary>Default sanity cap. See class doc for rationale.</summary>
    public const int Default = 5000;

    /// <summary>Env var name. Pinned by unit test (Rule 8).</summary>
    public const string MaxItemsEnvVar = "SQUID_PACKAGE_VERSION_MAX_ENUMERATE";

    /// <summary>
    /// Read the effective cap. Returns <see cref="Default"/> unless the env var is
    /// set to a positive integer.
    /// </summary>
    public static int Resolve()
    {
        var raw = Environment.GetEnvironmentVariable(MaxItemsEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return Default;

        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : Default;
    }
}
