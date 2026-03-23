using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class ScriptPodTemplateConfigMapTests
{
    private readonly KubernetesSettings _settings = new()
    {
        TentacleNamespace = "squid-ns",
        ScriptPodServiceAccount = "squid-script-sa",
        ScriptPodImage = "bitnami/kubectl:1.28",
        ScriptPodTimeoutSeconds = 1800,
        ScriptPodCpuRequest = "25m",
        ScriptPodMemoryRequest = "100Mi",
        ScriptPodCpuLimit = "500m",
        ScriptPodMemoryLimit = "512Mi",
        PvcClaimName = "squid-workspace",
        ScriptPodTemplateConfigMap = "squid-pod-template"
    };

    private const string TicketId = "abcdef123456789000";

    // ========================================================================
    // CRD takes precedence over ConfigMap
    // ========================================================================

    [Fact]
    public void TryLoadTemplate_CrdExists_UsesCrdNotConfigMap()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var crdTemplate = new ScriptPodTemplate { Image = "from-crd:v1" };

        var provider = new Mock<ScriptPodTemplateProvider>(null, _settings, ops.Object) { CallBase = false };
        provider.Setup(p => p.TryLoadTemplate(It.IsAny<string>())).Returns(crdTemplate);

        var manager = new KubernetesPodManager(ops.Object, _settings, provider.Object);
        var pod = CaptureCreatedPod(ops, manager);

        pod.Spec.Containers[0].Image.ShouldBe("from-crd:v1");
        ops.Verify(o => o.ListConfigMaps(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    // ========================================================================
    // ConfigMap fallback when CRD not found
    // ========================================================================

    [Fact]
    public void TryLoadFromConfigMap_ValidTemplate_ReturnsTemplate()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var template = new ScriptPodTemplate { Image = "from-configmap:v2" };
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        SetupConfigMap(ops, "squid-pod-template", json);

        var provider = new ScriptPodTemplateProvider(null, _settings, ops.Object);
        var result = provider.TryLoadFromConfigMap();

        result.ShouldNotBeNull();
        result.Image.ShouldBe("from-configmap:v2");
    }

    [Fact]
    public void TryLoadFromConfigMap_ConfigMapNotFound_ReturnsNull()
    {
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.ListConfigMaps("squid-ns", It.IsAny<string>()))
            .Returns(new V1ConfigMapList { Items = new List<V1ConfigMap>() });

        var provider = new ScriptPodTemplateProvider(null, _settings, ops.Object);
        var result = provider.TryLoadFromConfigMap();

        result.ShouldBeNull();
    }

    [Fact]
    public void TryLoadFromConfigMap_InvalidJson_ReturnsNull()
    {
        var ops = new Mock<IKubernetesPodOperations>();

        SetupConfigMap(ops, "squid-pod-template", "not-valid-json{{{");

        var provider = new ScriptPodTemplateProvider(null, _settings, ops.Object);
        var result = provider.TryLoadFromConfigMap();

        result.ShouldBeNull();
    }

    [Fact]
    public void TryLoadFromConfigMap_SettingEmpty_SkipsLookup()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var settings = new KubernetesSettings { TentacleNamespace = "squid-ns", ScriptPodTemplateConfigMap = "" };

        var provider = new ScriptPodTemplateProvider(null, settings, ops.Object);
        var result = provider.TryLoadFromConfigMap();

        result.ShouldBeNull();
        ops.Verify(o => o.ListConfigMaps(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void TryLoadFromConfigMap_NoOps_ReturnsNull()
    {
        var provider = new ScriptPodTemplateProvider(null, _settings, ops: null);
        var result = provider.TryLoadFromConfigMap();

        result.ShouldBeNull();
    }

    // ========================================================================
    // ConfigMap template applied to pod spec
    // ========================================================================

    [Fact]
    public void CreatePod_ConfigMapTemplate_AppliesOverridesToPodSpec()
    {
        var ops = new Mock<IKubernetesPodOperations>();

        var template = new ScriptPodTemplate
        {
            Image = "custom/from-configmap:latest",
            AdditionalEnvVars = new List<V1EnvVar> { new() { Name = "FROM_CM", Value = "true" } }
        };

        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        SetupConfigMap(ops, "squid-pod-template", json);

        var provider = new Mock<ScriptPodTemplateProvider>(null, _settings, ops.Object) { CallBase = false };
        provider.Setup(p => p.TryLoadTemplate(It.IsAny<string>())).Returns(() => provider.Object.TryLoadFromConfigMap());

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Returns((V1Pod pod, string ns) => pod);

        var manager = new KubernetesPodManager(ops.Object, _settings, provider.Object);
        var pod = CaptureCreatedPod(ops, manager);

        pod.Spec.Containers[0].Image.ShouldBe("custom/from-configmap:latest");
        pod.Spec.Containers[0].Env.ShouldNotBeNull();
        pod.Spec.Containers[0].Env.Any(e => e.Name == "FROM_CM" && e.Value == "true").ShouldBeTrue();
    }

    [Fact]
    public void TryLoadFromConfigMap_MissingTemplateKey_ReturnsNull()
    {
        var ops = new Mock<IKubernetesPodOperations>();

        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "squid-pod-template" },
            Data = new Dictionary<string, string> { ["wrong-key"] = "{}" }
        };

        ops.Setup(o => o.ListConfigMaps("squid-ns", It.IsAny<string>()))
            .Returns(new V1ConfigMapList { Items = new List<V1ConfigMap> { cm } });

        var provider = new ScriptPodTemplateProvider(null, _settings, ops.Object);
        var result = provider.TryLoadFromConfigMap();

        result.ShouldBeNull();
    }

    // ========================================================================
    // Template Caching (Fix 6)
    // ========================================================================

    [Fact]
    public void TryLoadTemplate_SecondCall_UsesCache()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var template = new ScriptPodTemplate { Image = "cached-image:v1" };
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        SetupConfigMap(ops, "squid-pod-template", json);

        var provider = new ScriptPodTemplateProvider(null, _settings, ops.Object);

        var result1 = provider.TryLoadFromConfigMap();
        // After first TryLoadTemplate call, cache is populated
        var templateResult = provider.TryLoadTemplate();
        var templateResult2 = provider.TryLoadTemplate();

        // ConfigMap lookup happens once for TryLoadFromConfigMap + once for first TryLoadTemplate
        // Second TryLoadTemplate uses cache
        templateResult2.ShouldNotBeNull();
        templateResult2.Image.ShouldBe("cached-image:v1");
    }

    [Fact]
    public void InvalidateCache_ForcesReload()
    {
        var ops = new Mock<IKubernetesPodOperations>();
        var template = new ScriptPodTemplate { Image = "cached-image:v1" };
        var json = JsonSerializer.Serialize(template, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        SetupConfigMap(ops, "squid-pod-template", json);

        var provider = new ScriptPodTemplateProvider(null, _settings, ops.Object);

        provider.TryLoadTemplate();
        provider.InvalidateCache();
        provider.TryLoadTemplate();

        // After invalidation, ConfigMap is queried again
        ops.Verify(o => o.ListConfigMaps(It.IsAny<string>(), It.IsAny<string>()), Times.AtLeast(2));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static void SetupConfigMap(Mock<IKubernetesPodOperations> ops, string name, string templateJson)
    {
        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = name },
            Data = new Dictionary<string, string> { ["template"] = templateJson }
        };

        ops.Setup(o => o.ListConfigMaps("squid-ns", It.IsAny<string>()))
            .Returns(new V1ConfigMapList { Items = new List<V1ConfigMap> { cm } });
    }

    private static V1Pod CaptureCreatedPod(Mock<IKubernetesPodOperations> ops, KubernetesPodManager manager)
    {
        V1Pod captured = null;

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        manager.CreatePod(TicketId);

        return captured;
    }
}
