using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

/// <summary>
/// P0-C.1 regression guard (2026-04-24 audit). Pre-fix,
/// <c>KubernetesSettings.ScriptPodImage</c> defaulted to
/// <c>bitnami/kubectl:latest</c> with <c>ImagePullPolicy=IfNotPresent</c>.
/// Two amplifying failure modes:
///
/// <list type="number">
///   <item>Floating <c>:latest</c> tag — a registry compromise / tag
///         repoint swaps the image under the cluster's nose. Every
///         script pod then executes attacker-chosen binaries with
///         whatever K8s permissions the tentacle SA has.</item>
///   <item><c>IfNotPresent</c> pull policy — once the compromised image
///         is pulled on any node, it stays cached until explicit pull.
///         Detection + remediation lags the actual attack by days.</item>
/// </list>
///
/// <para>Fix: the image MUST contain an <c>@sha256:…</c> digest pin.
/// Default setting value becomes empty string — fail-fast at pod
/// creation time with an actionable error telling the operator to pin
/// a digest. Opt-in escape hatch
/// <c>SQUID_ALLOW_UNPINNED_SCRIPT_POD_IMAGE=1</c> preserves tag-based
/// usage for dev / CI scenarios where the operator accepts the risk.</para>
/// </summary>
public sealed class ScriptPodImageValidationTests
{
    [Fact]
    public void AllowUnpinnedEnvVar_ConstantNamePinned()
    {
        // Renaming the env var would break dev / CI environments that
        // set it by name. Hard-pin to force intentional rename decisions.
        ScriptPodImageValidator.AllowUnpinnedEnvVar.ShouldBe("SQUID_ALLOW_UNPINNED_SCRIPT_POD_IMAGE");
    }

    [Theory]
    [InlineData("bitnami/kubectl@sha256:abc123def456789012345678901234567890123456789012345678901234aa77")]
    [InlineData("registry.example.com/kubectl:1.29@sha256:abc123def456789012345678901234567890123456789012345678901234aa77")]
    [InlineData("myorg/my-runner@sha256:0000000000000000000000000000000000000000000000000000000000000000")]
    public void DigestPinnedImage_PassesWithoutOptIn(string image)
    {
        Should.NotThrow(
            () => ScriptPodImageValidator.EnsureSafe(image, allowUnpinnedOverride: false),
            customMessage: "digest-pinned images are the intended state — must pass");
    }

    [Theory]
    [InlineData("bitnami/kubectl:latest", "explicit :latest tag — primary attack vector")]
    [InlineData("bitnami/kubectl", "no tag — implicit :latest")]
    [InlineData("bitnami/kubectl:1.29", "version tag but no digest pin — registry compromise still possible")]
    [InlineData("myregistry/image:v2", "private registry but still just a tag")]
    [InlineData("registry.example.com/kubectl:1.29.4", "FQDN with tag, no digest")]
    public void TagOnlyImage_ThrowsWithoutOptIn(string image, string rationale)
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => ScriptPodImageValidator.EnsureSafe(image, allowUnpinnedOverride: false),
            customMessage:
                $"tag-only image must be rejected. Rationale: {rationale}. " +
                "Regression here reopens the registry-compromise RCE vector for K8s script pods.");

        thrown.Message.ShouldContain("@sha256:",
            customMessage: "error must name the required digest format so operators know what to fix");
        thrown.Message.ShouldContain("SQUID_ALLOW_UNPINNED_SCRIPT_POD_IMAGE",
            customMessage: "error must name the opt-in env var for intentional unpinned use");
    }

    [Theory]
    [InlineData("bitnami/kubectl:latest")]
    [InlineData("bitnami/kubectl")]
    [InlineData("bitnami/kubectl:1.29")]
    public void TagOnlyImage_PassesWithExplicitOptIn(string image)
    {
        // Operator has acknowledged the risk via env var. MUST allow
        // tag-only image — otherwise dev environments that don't bother
        // with digest pinning can't run script pods at all.
        Should.NotThrow(
            () => ScriptPodImageValidator.EnsureSafe(image, allowUnpinnedOverride: true),
            customMessage: "explicit opt-in must permit tag-based images for dev / CI scenarios");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrNullImage_ThrowsRegardlessOfOptIn(string image)
    {
        // Empty image can never produce a valid K8s Pod spec — this is
        // the post-fix default, forcing operators to explicitly set a
        // value. Opt-in env var does NOT bypass this because empty image
        // is a configuration error, not a security trade-off.
        Should.Throw<InvalidOperationException>(
            () => ScriptPodImageValidator.EnsureSafe(image, allowUnpinnedOverride: true),
            customMessage: "empty/null image is a config error — must throw regardless of opt-in");
    }

    [Theory]
    [InlineData("bitnami/kubectl:1.29@sha256:tooshort")]                     // digest chars too few
    [InlineData("bitnami/kubectl@sha256:0000000000000000000000000000000000000000000000000000000000000000x")] // 65 chars — one over
    [InlineData("bitnami/kubectl@sha256:GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")] // non-hex
    public void InvalidDigestFormat_Throws(string image)
    {
        // Basic malformed-digest sanity. Defence-in-depth for pinning —
        // pattern must be `@sha256:` followed by exactly 64 hex chars.
        Should.Throw<InvalidOperationException>(
            () => ScriptPodImageValidator.EnsureSafe(image, allowUnpinnedOverride: false),
            customMessage: "malformed @sha256 digest (wrong length / non-hex) must be rejected");
    }
}
