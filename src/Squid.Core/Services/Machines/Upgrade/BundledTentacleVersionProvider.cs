using System.Reflection;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// Auto-detects the recommended Tentacle version from the running server's
/// own assembly metadata. Server and Tentacle release together (single GitHub
/// tag triggers both <c>build-api-docker.yml</c> and <c>build-publish-linux-tentacle.yml</c>),
/// so the server's <c>AssemblyInformationalVersion</c> is the authoritative
/// "what every agent should be on" answer — no separate text file to forget
/// to bump, no out-of-band coordination between the two release pipelines.
///
/// <para><b>Resolution order</b> (first non-empty wins):</para>
/// <list type="number">
///   <item>
///     Env var <c>SQUID_BUNDLED_TENTACLE_VERSION</c> — operator escape hatch
///     for forks, air-gapped deployments that ship a different Tentacle than
///     this server release, or local dev environments where the assembly
///     version is the meaningless <c>1.0.0</c> default.
///   </item>
///   <item>
///     <c>AssemblyInformationalVersion</c> on this assembly — populated by
///     <c>dotnet publish -p:Version=$IMAGE_TAG</c> in <c>Dockerfile.Api</c>.
///     CI's GitVersion step computes the same tag the Tentacle workflow
///     publishes the tarball under, so they always match.
///   </item>
///   <item>
///     <c>AssemblyVersion</c> as a final fallback — populated even without
///     the build-time injection, but loses the GitVersion suffix
///     (<c>1.4.0+sha</c> → <c>1.4.0</c>). Acceptable; semver-major.minor.patch
///     is what matters for the URL.
///   </item>
/// </list>
/// </summary>
public sealed class BundledTentacleVersionProvider : IBundledTentacleVersionProvider
{
    public const string OverrideEnvVar = "SQUID_BUNDLED_TENTACLE_VERSION";

    private const string DownloadUrlTemplate =
        "https://github.com/SolarifyDev/Squid/releases/download/{0}/squid-tentacle-{0}-{1}.tar.gz";

    private static readonly Lazy<string> _cachedVersion = new(LoadVersion);

    public string GetBundledVersion() => _cachedVersion.Value;

    public string GetDownloadUrl(string version, string rid)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("version is required", nameof(version));
        if (string.IsNullOrWhiteSpace(rid))
            throw new ArgumentException("rid is required", nameof(rid));

        return string.Format(DownloadUrlTemplate, version.Trim(), rid.Trim());
    }

    private static string LoadVersion()
    {
        var fromEnv = Environment.GetEnvironmentVariable(OverrideEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return CleanSemver(fromEnv);

        var asm = typeof(BundledTentacleVersionProvider).Assembly;

        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(infoVersion))
            return CleanSemver(infoVersion);

        var asmVersion = asm.GetName().Version?.ToString();
        if (!string.IsNullOrWhiteSpace(asmVersion) && asmVersion != "0.0.0.0")
            return CleanSemver(asmVersion);

        Log.Warning(
            "Bundled Tentacle version could not be auto-detected from {Assembly}. " +
            "Set {EnvVar} to override, or build with -p:Version=<semver>. " +
            "Server cannot recommend an upgrade target — operators must specify TargetVersion explicitly.",
            asm.GetName().Name, OverrideEnvVar);
        return string.Empty;
    }

    /// <summary>
    /// Strip GitVersion build-metadata suffix (<c>+sha</c>) and 4th .NET
    /// assembly-version segment (<c>1.4.0.0</c> → <c>1.4.0</c>) so the result
    /// is a clean semver suitable for the GitHub Releases tag URL.
    /// </summary>
    private static string CleanSemver(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var trimmed = raw.Trim();
        var plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];

        // Collapse 1.4.0.0 → 1.4.0 (4th segment is .NET assembly artefact, not semver).
        var parts = trimmed.Split('.');
        if (parts.Length == 4 && int.TryParse(parts[3], out var build) && build == 0)
            trimmed = string.Join('.', parts.Take(3));

        return trimmed;
    }
}
