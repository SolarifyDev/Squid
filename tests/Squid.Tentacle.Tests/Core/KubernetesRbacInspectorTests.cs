using Shouldly;
using Squid.Tentacle.Core;
using Squid.Tentacle.Tests.Support;
using Xunit;

namespace Squid.Tentacle.Tests.Core;

/// <summary>
/// P0-Phase10.1 (audit C.3) — agent-side RBAC probe pin.
///
/// <para><b>Why this exists</b>: a K8s pod's ServiceAccount RBAC is the
/// gating factor for whether deployments will actually succeed. Pre-fix,
/// the agent reported "healthy" via Halibut polling regardless of RBAC
/// state. Now <see cref="KubernetesRbacInspector"/> shells out to
/// <c>kubectl auth can-i &lt;verb&gt; &lt;resource&gt;</c> at startup and
/// surfaces the result via <see cref="Squid.Message.Contracts.Tentacle.CapabilitiesResponse.Metadata"/>.</para>
///
/// <para>Tests focus on the metadata-key contract + parsing logic
/// (kubectl invocation is integration-level and validated via the K8s
/// E2E suite, not here).</para>
/// </summary>
[Trait("Category", TentacleTestCategories.Core)]
public sealed class KubernetesRbacInspectorTests
{
    [Fact]
    public void MetadataKeys_PinnedLiterals()
    {
        // Operators reference these in alerting / runbooks; renaming
        // breaks every tenant's alert query. Mirrors the server-side
        // KubernetesAgentHealthCheckStrategy keys exactly — drift between
        // the two would silently break the health check.
        KubernetesRbacInspector.MetaCanCreatePods.ShouldBe("kubernetes.canCreatePods");
        KubernetesRbacInspector.MetaCanCreateConfigMaps.ShouldBe("kubernetes.canCreateConfigMaps");
        KubernetesRbacInspector.MetaCanCreateSecrets.ShouldBe("kubernetes.canCreateSecrets");
    }

    [Theory]
    [InlineData(0, "yes", "exit 0 = permission granted")]
    [InlineData(1, "no",  "exit 1 = permission denied")]
    [InlineData(2, "no",  "any non-zero = treat as denied for safety")]
    public void ParseAuthCanIExitCode_MapsToYesNo(int exitCode, string expected, string scenario)
    {
        // Pure unit test: kubectl exit code 0 = yes, anything else = no.
        // Defensive default: any non-zero exit (including kubectl missing,
        // RBAC API error, etc.) is reported as "no" so the operator gets
        // a fail-closed signal rather than a silent pass.
        KubernetesRbacInspector.ParseAuthCanIExitCode(exitCode).ShouldBe(expected, customMessage: scenario);
    }

    [Fact]
    public void IsRunningInKubernetesPod_NoEnvVar_ReturnsFalse()
    {
        // The agent should ONLY probe RBAC when running inside a K8s pod
        // (KUBERNETES_SERVICE_HOST is automounted by kubelet). Outside K8s
        // the probe would fail with "kubectl not found" → noise.
        var previous = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);

        try
        {
            KubernetesRbacInspector.IsRunningInKubernetesPod().ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", previous);
        }
    }

    [Fact]
    public void IsRunningInKubernetesPod_EnvVarSet_ReturnsTrue()
    {
        var previous = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", "10.96.0.1");

        try
        {
            KubernetesRbacInspector.IsRunningInKubernetesPod().ShouldBeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", previous);
        }
    }

    [Fact]
    public void Inspect_NotInKubernetesPod_ReturnsEmpty()
    {
        // No K8s context → no probe → empty metadata. Server-side health
        // check tolerates absent keys (backward compat path covers
        // non-K8s tentacles too).
        var previous = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", null);

        try
        {
            var result = KubernetesRbacInspector.Inspect();
            result.ShouldBeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable("KUBERNETES_SERVICE_HOST", previous);
        }
    }
}
