using Squid.Message.Hardening;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

/// <summary>
/// P0-C.1 regression guard, refactored under the project-wide three-mode
/// hardening pattern (CLAUDE.md §"Hardening Three-Mode Enforcement").
///
/// <para>Pre-fix, <c>KubernetesSettings.ScriptPodImage</c> defaulted to
/// <c>bitnami/kubectl:latest</c> with <c>ImagePullPolicy=IfNotPresent</c> —
/// floating tag + cached pull policy meant a registry compromise / tag repoint
/// silently substituted attacker-chosen binaries into every script pod.</para>
///
/// <para>Fix: <see cref="ScriptPodImageValidator.EnsureSafe"/> requires the
/// image reference to contain <c>@sha256:&lt;64-hex&gt;</c>. Behaviour depends
/// on the <see cref="EnforcementMode"/> resolved from
/// <see cref="ScriptPodImageValidator.EnforcementEnvVar"/>: Off (silent allow),
/// Warn (default — allow + structured warning), Strict (reject + throw).
/// Backward compat for existing deploys is preserved by Warn-as-default.</para>
/// </summary>
public sealed class ScriptPodImageValidationTests
{
    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        ScriptPodImageValidator.EnforcementEnvVar.ShouldBe("SQUID_SCRIPT_POD_IMAGE_ENFORCEMENT");
    }

    // ── Happy path: digest-pinned passes in every mode ───────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off,    "bitnami/kubectl@sha256:abc123def456789012345678901234567890123456789012345678901234aa77")]
    [InlineData(EnforcementMode.Warn,   "registry.example.com/kubectl:1.29@sha256:abc123def456789012345678901234567890123456789012345678901234aa77")]
    [InlineData(EnforcementMode.Strict, "myorg/my-runner@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public void DigestPinnedImage_PassesInAnyMode(EnforcementMode mode, string image)
    {
        Should.NotThrow(
            () => ScriptPodImageValidator.EnsureSafe(image, mode),
            customMessage: $"digest-pinned image is the intended state — must pass in {mode}");
    }

    // ── Strict mode: tag-only and empty are both rejected ────────────────────

    [Theory]
    [InlineData("bitnami/kubectl:latest", "explicit :latest tag — primary attack vector")]
    [InlineData("bitnami/kubectl", "no tag — implicit :latest")]
    [InlineData("bitnami/kubectl:1.29", "version tag but no digest pin — registry compromise still possible")]
    [InlineData("myregistry/image:v2", "private registry but still just a tag")]
    [InlineData("registry.example.com/kubectl:1.29.4", "FQDN with tag, no digest")]
    public void Strict_TagOnlyImage_Throws(string image, string rationale)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => ScriptPodImageValidator.EnsureSafe(image, EnforcementMode.Strict),
            customMessage:
                $"Strict mode rejects tag-only image. Rationale: {rationale}. Regression here " +
                "reopens the registry-compromise RCE vector for K8s script pods.");

        thrown.Message.ShouldContain("@sha256:",
            customMessage: "error must name the required digest format so operators know what to fix");
        thrown.Message.ShouldContain(ScriptPodImageValidator.EnforcementEnvVar,
            customMessage: "error must name the env var operators can flip to bypass");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Strict_EmptyImage_Throws(string image)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => ScriptPodImageValidator.EnsureSafe(image, EnforcementMode.Strict));

        thrown.Message.ShouldContain("ScriptPodImage");
        thrown.Message.ShouldContain(ScriptPodImageValidator.EnforcementEnvVar);
    }

    // ── Warn mode (default): backward compat — allow but log ────────────────

    [Theory]
    [InlineData("bitnami/kubectl:latest")]
    [InlineData("bitnami/kubectl")]
    [InlineData("bitnami/kubectl:1.29")]
    public void Warn_TagOnlyImage_DoesNotThrow_BackwardCompat(string image)
    {
        // P0 fix MUST NOT break existing deploys that ship with tag-only images.
        // Warn mode (default) lets pod creation proceed; warning lands in logs.
        Should.NotThrow(
            () => ScriptPodImageValidator.EnsureSafe(image, EnforcementMode.Warn),
            customMessage:
                "Warn mode must NOT throw on tag-only image. Pre-Phase-3 default " +
                "behaviour was Strict and broke deploys at first pod creation. The whole " +
                "point of this refactor is that 'warn-then-eventually-flip' allows time " +
                "for ops to remediate.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Warn_EmptyImage_DoesNotThrow(string image)
    {
        // K8s API itself will reject the resulting empty Image at pod-create time.
        // We don't add a redundant pre-check throw — the warning at validate time
        // gives operators time to fix before they hit the API error.
        Should.NotThrow(
            () => ScriptPodImageValidator.EnsureSafe(image, EnforcementMode.Warn));
    }

    // ── Off mode: silent allow ───────────────────────────────────────────────

    [Theory]
    [InlineData("bitnami/kubectl:latest")]
    [InlineData(null)]
    [InlineData("")]
    public void Off_AnyImage_AcceptsSilently(string image)
    {
        Should.NotThrow(() => ScriptPodImageValidator.EnsureSafe(image, EnforcementMode.Off));
    }

    // ── Malformed @sha256 marker: ALWAYS throws ──────────────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off,    "bitnami/kubectl:1.29@sha256:tooshort")]
    [InlineData(EnforcementMode.Warn,   "bitnami/kubectl@sha256:0000000000000000000000000000000000000000000000000000000000000000x")] // 65 chars
    [InlineData(EnforcementMode.Strict, "bitnami/kubectl@sha256:GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")] // non-hex
    public void AnyMode_MalformedDigest_AlwaysThrows(EnforcementMode mode, string image)
    {
        // Malformed @sha256 marker means the operator CLEARLY tried to pin and got
        // the format wrong. No mode can recover — accepting silently would be much
        // worse than failing loudly. Mode only governs the "tag-only-vs-pinned"
        // choice, not "is this even a valid digest format".
        var thrown = Should.Throw<InvalidOperationException>(
            () => ScriptPodImageValidator.EnsureSafe(image, mode),
            customMessage: $"malformed @sha256 must throw even in {mode} — operator intent was to pin");

        thrown.Message.ShouldContain("malformed");
        thrown.Message.ShouldContain("unconditional",
            customMessage: "error must explain why this rejection ignores the enforcement mode");
    }
}
