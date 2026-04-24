using System.Text.RegularExpressions;
using Squid.Message.Hardening;

namespace Squid.Tentacle.Kubernetes;

/// <summary>
/// Validates the image reference configured for Script-Pod execution
/// (<c>KubernetesSettings.ScriptPodImage</c>) before it flows into a
/// <c>V1Container.Image</c>. The intent is to enforce an <c>@sha256:...</c>
/// digest pin so a registry compromise or tag repoint can't silently swap a
/// malicious image into the cluster where the tentacle runs.
///
/// <para>Follows the project-wide three-mode hardening pattern (CLAUDE.md
/// §"Hardening Three-Mode Enforcement"). Default is
/// <see cref="EnforcementMode.Warn"/> — preserves backward compat for deploys
/// shipped pre-hardening, surfaces tech debt in logs. Operators flip
/// <see cref="EnforcementEnvVar"/> to <c>strict</c> for production hardening.</para>
/// </summary>
public static class ScriptPodImageValidator
{
    /// <summary>
    /// Env var that selects enforcement mode for script-pod image validation.
    /// Recognised values: <c>off</c> / <c>warn</c> / <c>strict</c>; default
    /// (unset / blank) is <see cref="EnforcementMode.Warn"/>.
    ///
    /// <para>Pinned literal — renaming breaks operators who set the env var
    /// by its documented name.</para>
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT";

    // Format: '<registry/repo[:tag]>@sha256:<64-hex-chars>'. The part before @
    // is unconstrained (too many valid forms across registries). We assert only
    // that the digest suffix is present and well-formed.
    private static readonly Regex DigestPattern =
        new(@"@sha256:[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    /// <summary>
    /// Validate <paramref name="image"/> against the digest-pin requirement
    /// under <paramref name="mode"/>.
    ///
    /// <para><b>Behaviour matrix</b>:</para>
    /// <list type="table">
    ///   <item><term>null / empty / whitespace</term>
    ///         <description>Off → return; Warn → log warning + return; Strict → throw.</description></item>
    ///   <item><term>has '@sha256:' marker but malformed (wrong length / non-hex)</term>
    ///         <description>ALWAYS throw. The operator tried to pin and got the
    ///         format wrong — accepting silently would be worse than failing
    ///         loudly. No mode can save this.</description></item>
    ///   <item><term>tag-only (no @sha256: suffix)</term>
    ///         <description>Off → return; Warn → log warning + return; Strict → throw.</description></item>
    ///   <item><term>digest-pinned</term>
    ///         <description>All modes return silently — happy path.</description></item>
    /// </list>
    /// </summary>
    public static void EnsureSafe(string? image, EnforcementMode mode)
    {
        const string settingPath = "Kubernetes:ScriptPodImage";

        if (string.IsNullOrWhiteSpace(image))
        {
            EnforceEmpty(mode, settingPath);
            return;
        }

        // Malformed @sha256 marker is ALWAYS rejected — the operator clearly
        // intended to pin and got the format wrong. Mode only governs the
        // tag-only-vs-pinned choice, not "is this even a valid digest format".
        if (image.Contains("@sha256:", StringComparison.Ordinal) && !DigestPattern.IsMatch(image))
            throw new InvalidOperationException(
                $"ScriptPodImage '{image}' has an '@sha256:' marker but the digest is malformed " +
                "(expected exactly 64 hex characters after the colon). Verify the digest you " +
                "copied from your registry UI. This rejection is unconditional regardless of the " +
                $"{EnforcementEnvVar} mode — mode only governs whether tag-only is accepted, not " +
                "whether broken-digest counts as pinned.");

        if (DigestPattern.IsMatch(image)) return;

        EnforceTagOnly(mode, image, settingPath);
    }

    private static void EnforceEmpty(EnforcementMode mode, string settingPath)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return;

            case EnforcementMode.Warn:
                Serilog.Log.Warning(
                    "ScriptPodImage at {SettingPath} is empty. The downstream V1Container.Image will " +
                    "be set to the empty string and pod creation will fail at the K8s API. Set " +
                    "{SettingPath} to a digest-pinned image reference like " +
                    "'bitnami/kubectl@sha256:<64-hex>'. Set {EnvVar}=strict to refuse start instead " +
                    "of failing at pod-create time.",
                    settingPath, EnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"ScriptPodImage at {settingPath} is empty. Set it (in appsettings.json or via env " +
                    $"var Kubernetes__ScriptPodImage) to a digest-pinned image reference, e.g. " +
                    $"'bitnami/kubectl@sha256:<64-hex>'. To suppress this rejection, set " +
                    $"{EnforcementEnvVar}=warn (allow + log warning) or {EnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private static void EnforceTagOnly(EnforcementMode mode, string image, string settingPath)
    {
        switch (mode)
        {
            case EnforcementMode.Off:
                return;

            case EnforcementMode.Warn:
                Serilog.Log.Warning(
                    "ScriptPodImage '{Image}' (from {SettingPath}) is tag-only, not digest-pinned. A " +
                    "registry compromise or tag repoint would silently substitute attacker code. " +
                    "Replace with '{ImageBase}@sha256:<64-hex>' from your registry. Set " +
                    "{EnvVar}=strict to refuse the tag-only reference instead of just warning.",
                    image, settingPath, image.Split(':')[0], EnforcementEnvVar);
                return;

            case EnforcementMode.Strict:
                throw new InvalidOperationException(
                    $"ScriptPodImage '{image}' is not digest-pinned. The reference MUST include " +
                    $"'@sha256:<64-hex>' to prevent registry-compromise / tag-repoint RCE. Replace " +
                    $"with the digest form (e.g. '{image.Split(':')[0]}@sha256:<64-hex>'). To " +
                    $"suppress this rejection, set {EnforcementEnvVar}=warn (allow + log warning) " +
                    $"or {EnforcementEnvVar}=off (silent).");

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    /// <summary>
    /// Resolve the enforcement mode from <see cref="EnforcementEnvVar"/>. Used
    /// by call sites that don't override the mode explicitly.
    /// </summary>
    internal static EnforcementMode ReadMode()
        => EnforcementModeReader.Read(EnforcementEnvVar);
}
