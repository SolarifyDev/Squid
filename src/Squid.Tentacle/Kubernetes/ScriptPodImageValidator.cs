using System.Text.RegularExpressions;

namespace Squid.Tentacle.Kubernetes;

/// <summary>
/// Validates the image reference configured for Script-Pod execution
/// (<c>KubernetesSettings.ScriptPodImage</c>) before it flows into a
/// <c>V1Container.Image</c>. Enforces an <c>@sha256:...</c> digest pin
/// so a registry compromise or tag repoint can't silently swap a
/// malicious image into the cluster where the tentacle runs.
///
/// <para>See <c>ScriptPodImageValidationTests</c> for the full decision
/// matrix. Env-var escape hatch <see cref="AllowUnpinnedEnvVar"/>
/// preserves tag-based usage for dev / CI where the operator
/// knowingly accepts the risk.</para>
/// </summary>
public static class ScriptPodImageValidator
{
    /// <summary>
    /// Env-var escape hatch for dev / CI scenarios where the operator
    /// knowingly runs a tag-based image (<c>bitnami/kubectl:latest</c>
    /// or similar) without digest pinning. When set to <c>1</c> /
    /// <c>true</c> / <c>yes</c> (case-insensitive), <see cref="EnsureSafe"/>
    /// accepts images without <c>@sha256:</c> digest suffix — with a
    /// log warning documenting the acceptance of risk.
    ///
    /// <para>Default behaviour (env var unset) is fail-closed: tentacle
    /// refuses to create a script pod with a tag-only image, protecting
    /// the cluster from registry compromise / tag repoint RCE.</para>
    ///
    /// <para>Pinned literal — renaming breaks dev environments that set
    /// the env var by its documented name.</para>
    /// </summary>
    public const string AllowUnpinnedEnvVar = "SQUID_ALLOW_UNPINNED_SCRIPT_POD_IMAGE";

    // Format: '<something>@sha256:<64-hex-chars>' somewhere in the string.
    // The part before @ can be any registry/repository path; we don't
    // validate it (too many valid forms). We only assert the digest
    // suffix is present and well-formed.
    private static readonly Regex DigestPattern =
        new(@"@sha256:[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="image"/>
    /// is unsuitable for production script-pod execution:
    /// <list type="bullet">
    ///   <item>null / empty / whitespace → always throws (config error,
    ///         no opt-in bypass)</item>
    ///   <item>missing <c>@sha256:&lt;64-hex&gt;</c> suffix → throws unless
    ///         <paramref name="allowUnpinnedOverride"/> is true</item>
    ///   <item>malformed digest (wrong length / non-hex) → always throws
    ///         (the user tried to pin but got the format wrong)</item>
    /// </list>
    /// </summary>
    public static void EnsureSafe(string? image, bool allowUnpinnedOverride)
    {
        const string settingPath = "Kubernetes:ScriptPodImage";

        if (string.IsNullOrWhiteSpace(image))
            throw new InvalidOperationException(
                $"ScriptPodImage is empty. Set {settingPath} in appsettings.json (or via env var " +
                $"Kubernetes__ScriptPodImage) to a digest-pinned image reference, e.g. " +
                "'bitnami/kubectl@sha256:<64-hex>'. Unpinned tag-only images are rejected because " +
                "a registry compromise or tag repoint would give the attacker arbitrary code " +
                "execution inside the cluster where this tentacle runs.");

        // Even under opt-in, an explicitly malformed @sha256 suffix still
        // throws — the operator tried to pin something but got the format
        // wrong. We'd rather surface that loudly than silently fall back
        // to tag-only acceptance.
        if (image.Contains("@sha256:", StringComparison.Ordinal) && !DigestPattern.IsMatch(image))
            throw new InvalidOperationException(
                $"ScriptPodImage '{image}' has an '@sha256:' marker but the digest is malformed " +
                "(expected exactly 64 hex characters after the colon). Verify the digest you " +
                "copied from your registry UI — it should look like " +
                "'@sha256:abc123...' with 64 total hex characters.");

        if (DigestPattern.IsMatch(image)) return;

        if (allowUnpinnedOverride)
        {
            Serilog.Log.Warning(
                "ScriptPodImage '{Image}' is not digest-pinned ('@sha256:' missing) but {EnvVar}=1 " +
                "is set — proceeding with the tag-based reference. A registry compromise or tag " +
                "repoint would execute attacker code in the cluster. Use digest pinning in " +
                "production deploys.",
                image, AllowUnpinnedEnvVar);
            return;
        }

        throw new InvalidOperationException(
            $"ScriptPodImage '{image}' is not digest-pinned. The reference MUST include " +
            $"'@sha256:<64-hex>' to prevent registry-compromise / tag-repoint RCE. Replace with " +
            $"the digest form (e.g. '{image.Split(':')[0]}@sha256:<64-hex>'). For dev / CI " +
            $"where you knowingly accept the risk of tag-based references, set " +
            $"{AllowUnpinnedEnvVar}=1 to opt in.");
    }

    /// <summary>
    /// Reads the escape-hatch env var, returning true iff it is set to
    /// a recognised truthy value (1 / true / yes, case-insensitive).
    /// </summary>
    internal static bool ReadAllowUnpinnedOverride()
    {
        var raw = System.Environment.GetEnvironmentVariable(AllowUnpinnedEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalized = raw.Trim().ToLowerInvariant();

        return normalized.Equals("1", StringComparison.Ordinal)
            || normalized.Equals("true", StringComparison.Ordinal)
            || normalized.Equals("yes", StringComparison.Ordinal);
    }
}
