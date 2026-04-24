using System.Collections.Generic;
using System.Linq;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class ScriptPodTemplateMergeTests
{
    // Digest-pinned images required post P0-C.1 — BuildPodSpec rejects tag-only references.
    private const string DefaultDigestImage =
        "bitnami/kubectl@sha256:abc123def456789012345678901234567890123456789012345678901234aa77";

    private const string CustomDigestImage =
        "custom/image@sha256:deadbeef0000000000000000000000000000000000000000000000000000cafe";

    private readonly KubernetesSettings _settings = new()
    {
        TentacleNamespace = "squid-ns",
        ScriptPodServiceAccount = "squid-script-sa",
        ScriptPodImage = DefaultDigestImage,
        ScriptPodTimeoutSeconds = 1800,
        ScriptPodCpuRequest = "25m",
        ScriptPodMemoryRequest = "100Mi",
        ScriptPodCpuLimit = "500m",
        ScriptPodMemoryLimit = "512Mi",
        PvcClaimName = "squid-workspace"
    };

    private const string TicketId = "abcdef123456789000";

    // ========================================================================
    // No template — default behavior preserved
    // ========================================================================

    [Fact]
    public void CreatePod_NoTemplate_UsesDefaultImage()
    {
        var pod = CaptureCreatedPod(template: null);

        pod.Spec.Containers[0].Image.ShouldBe(DefaultDigestImage);
    }

    [Fact]
    public void CreatePod_NoTemplate_UsesDefaultResources()
    {
        var pod = CaptureCreatedPod(template: null);

        pod.Spec.Containers[0].Resources.Requests["cpu"].ToString().ShouldBe("25m");
    }

    // ========================================================================
    // Template image override
    // ========================================================================

    [Fact]
    public void CreatePod_TemplateOverridesImage()
    {
        var template = new ScriptPodTemplate { Image = CustomDigestImage };

        var pod = CaptureCreatedPod(template);

        pod.Spec.Containers[0].Image.ShouldBe(CustomDigestImage);
    }

    [Fact]
    public void CreatePod_TemplateEmptyImage_KeepsDefault()
    {
        var template = new ScriptPodTemplate { Image = "" };

        var pod = CaptureCreatedPod(template);

        pod.Spec.Containers[0].Image.ShouldBe(DefaultDigestImage);
    }

    [Fact]
    public void CreatePod_TemplateTagOnlyImage_StrictMode_Throws()
    {
        // P0-C.1 defence-in-depth: the template.Image override must ALSO flow
        // through ScriptPodImageValidator under the resolved enforcement mode.
        // We force Strict via env var so the wiring assertion is mode-independent
        // (the validator's own mode matrix is in ScriptPodImageValidationTests).
        var previous = System.Environment.GetEnvironmentVariable(
            Squid.Tentacle.Kubernetes.ScriptPodImageValidator.EnforcementEnvVar);
        System.Environment.SetEnvironmentVariable(
            Squid.Tentacle.Kubernetes.ScriptPodImageValidator.EnforcementEnvVar, "strict");

        try
        {
            var template = new ScriptPodTemplate { Image = "custom/image:v2" };

            var thrown = Should.Throw<InvalidOperationException>(
                () => CaptureCreatedPod(template),
                customMessage:
                    "with Strict env var set, template.Image override must be validated. " +
                    "Regression here lets a tag-only template image bypass _settings pinning " +
                    "and reopen the registry-compromise RCE vector.");

            thrown.Message.ShouldContain("@sha256:",
                customMessage: "error must name the required digest format");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(
                Squid.Tentacle.Kubernetes.ScriptPodImageValidator.EnforcementEnvVar, previous);
        }
    }

    // ========================================================================
    // Template resource override
    // ========================================================================

    [Fact]
    public void CreatePod_TemplateOverridesResources()
    {
        var template = new ScriptPodTemplate
        {
            Resources = new V1ResourceRequirements
            {
                Requests = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new("100m"),
                    ["memory"] = new("256Mi")
                },
                Limits = new Dictionary<string, ResourceQuantity>
                {
                    ["cpu"] = new("1"),
                    ["memory"] = new("1Gi")
                }
            }
        };

        var pod = CaptureCreatedPod(template);

        pod.Spec.Containers[0].Resources.Requests["cpu"].ToString().ShouldBe("100m");
        pod.Spec.Containers[0].Resources.Limits["memory"].ToString().ShouldBe("1Gi");
    }

    // ========================================================================
    // Template tolerations override
    // ========================================================================

    [Fact]
    public void CreatePod_TemplateOverridesTolerations()
    {
        var template = new ScriptPodTemplate
        {
            Tolerations = new List<V1Toleration>
            {
                new() { Key = "dedicated", OperatorProperty = "Equal", Value = "squid", Effect = "NoSchedule" }
            }
        };

        var pod = CaptureCreatedPod(template);

        pod.Spec.Tolerations.ShouldNotBeNull();
        pod.Spec.Tolerations.Count.ShouldBe(1);
        pod.Spec.Tolerations[0].Key.ShouldBe("dedicated");
    }

    // ========================================================================
    // Template nodeSelector
    // ========================================================================

    [Fact]
    public void CreatePod_TemplateAddsNodeSelector()
    {
        var template = new ScriptPodTemplate
        {
            NodeSelector = new Dictionary<string, string> { ["role"] = "worker" }
        };

        var pod = CaptureCreatedPod(template);

        pod.Spec.NodeSelector.ShouldContainKeyAndValue("role", "worker");
    }

    // ========================================================================
    // Template additional volumes
    // ========================================================================

    [Fact]
    public void CreatePod_TemplateAddsExtraVolumes()
    {
        var template = new ScriptPodTemplate
        {
            AdditionalVolumes = new List<V1Volume>
            {
                new() { Name = "config", ConfigMap = new V1ConfigMapVolumeSource { Name = "app-config" } }
            },
            AdditionalVolumeMounts = new List<V1VolumeMount>
            {
                new() { Name = "config", MountPath = "/etc/config" }
            }
        };

        var pod = CaptureCreatedPod(template);

        pod.Spec.Volumes.Any(v => v.Name == "config").ShouldBeTrue();
        pod.Spec.Volumes.Any(v => v.Name == "workspace").ShouldBeTrue(); // original still exists

        pod.Spec.Containers[0].VolumeMounts.Any(m => m.Name == "config").ShouldBeTrue();
        pod.Spec.Containers[0].VolumeMounts.Any(m => m.Name == "workspace").ShouldBeTrue();
    }

    // ========================================================================
    // Template additional env vars
    // ========================================================================

    [Fact]
    public void CreatePod_TemplateAddsEnvVars()
    {
        var template = new ScriptPodTemplate
        {
            AdditionalEnvVars = new List<V1EnvVar>
            {
                new() { Name = "LOG_LEVEL", Value = "debug" }
            }
        };

        var pod = CaptureCreatedPod(template);

        pod.Spec.Containers[0].Env.ShouldNotBeNull();
        pod.Spec.Containers[0].Env.Any(e => e.Name == "LOG_LEVEL" && e.Value == "debug").ShouldBeTrue();
    }

    // ========================================================================
    // Helper
    // ========================================================================

    private V1Pod CaptureCreatedPod(ScriptPodTemplate template)
    {
        V1Pod captured = null;
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var templateProvider = new Mock<ScriptPodTemplateProvider>(null, null, null) { CallBase = false };
        templateProvider.Setup(p => p.TryLoadTemplate(It.IsAny<string>())).Returns(template);

        var manager = new KubernetesPodManager(ops.Object, _settings, templateProvider.Object);
        manager.CreatePod(TicketId);

        return captured;
    }
}
