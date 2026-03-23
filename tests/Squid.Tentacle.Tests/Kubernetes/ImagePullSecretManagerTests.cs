using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class ImagePullSecretManagerTests
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();

    private readonly KubernetesSettings _settings = new()
    {
        TentacleNamespace = "squid-ns",
        ScriptPodRegistryServer = "registry.example.com",
        ScriptPodRegistryUsername = "user",
        ScriptPodRegistryPassword = "pass123"
    };

    // ========================================================================
    // EnsurePullSecret (single registry backward compat)
    // ========================================================================

    [Fact]
    public void EnsurePullSecret_CreatesDockerConfigJsonSecret()
    {
        V1Secret captured = null;

        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), "squid-ns"))
            .Callback<V1Secret, string>((s, ns) => captured = s)
            .Returns((V1Secret s, string ns) => s);

        var manager = new ImagePullSecretManager(_ops.Object, _settings);
        var name = manager.EnsurePullSecret();

        name.ShouldNotBeNull();
        captured.ShouldNotBeNull();
        captured.Type.ShouldBe("kubernetes.io/dockerconfigjson");
        captured.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
        captured.Data.ShouldContainKey(".dockerconfigjson");

        var dockerConfigJson = Encoding.UTF8.GetString(captured.Data[".dockerconfigjson"]);
        var config = JsonDocument.Parse(dockerConfigJson);
        config.RootElement.GetProperty("auths").GetProperty("registry.example.com").GetProperty("username").GetString().ShouldBe("user");
        config.RootElement.GetProperty("auths").GetProperty("registry.example.com").GetProperty("password").GetString().ShouldBe("pass123");
    }

    [Fact]
    public void EnsurePullSecret_NoCredentials_ReturnsNull()
    {
        var settings = new KubernetesSettings { TentacleNamespace = "squid-ns", ScriptPodRegistryServer = "" };
        var manager = new ImagePullSecretManager(_ops.Object, settings);

        var name = manager.EnsurePullSecret();

        name.ShouldBeNull();
        _ops.Verify(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void EnsurePullSecret_CalledTwice_CreatesOnce()
    {
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);

        var manager = new ImagePullSecretManager(_ops.Object, _settings);
        manager.EnsurePullSecret();
        manager.EnsurePullSecret();

        _ops.Verify(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void EnsurePullSecret_ApiFailure_ReturnsNull()
    {
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Throws(new Exception("K8s API unavailable"));

        var manager = new ImagePullSecretManager(_ops.Object, _settings);
        var name = manager.EnsurePullSecret();

        name.ShouldBeNull();
    }

    [Fact]
    public void HasCredentials_WithServer_ReturnsTrue()
    {
        var manager = new ImagePullSecretManager(_ops.Object, _settings);

        manager.HasCredentials.ShouldBeTrue();
    }

    [Fact]
    public void HasCredentials_EmptyServer_ReturnsFalse()
    {
        var settings = new KubernetesSettings { ScriptPodRegistryServer = "" };
        var manager = new ImagePullSecretManager(_ops.Object, settings);

        manager.HasCredentials.ShouldBeFalse();
    }

    // ========================================================================
    // BuildDockerConfigJson
    // ========================================================================

    [Fact]
    public void BuildDockerConfigJson_ProducesValidFormat()
    {
        var json = ImagePullSecretManager.BuildDockerConfigJson("docker.io", "myuser", "mypass");

        var doc = JsonDocument.Parse(json);
        var auths = doc.RootElement.GetProperty("auths");
        var entry = auths.GetProperty("docker.io");

        entry.GetProperty("username").GetString().ShouldBe("myuser");
        entry.GetProperty("password").GetString().ShouldBe("mypass");

        var expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("myuser:mypass"));
        entry.GetProperty("auth").GetString().ShouldBe(expectedAuth);
    }

    // ========================================================================
    // BuildSecretName (Fix 4)
    // ========================================================================

    [Fact]
    public void BuildSecretName_DeterministicFromServerAndUsername()
    {
        var name1 = ImagePullSecretManager.BuildSecretName("docker.io", "user1");
        var name2 = ImagePullSecretManager.BuildSecretName("docker.io", "user1");
        var name3 = ImagePullSecretManager.BuildSecretName("gcr.io", "user2");

        name1.ShouldBe(name2);
        name1.ShouldNotBe(name3);
        name1.ShouldStartWith("squid-registry-");
    }

    // ========================================================================
    // Multi-Registry (Fix 4)
    // ========================================================================

    [Fact]
    public void GetAllRegistries_SingleRegistry_ReturnsList()
    {
        var manager = new ImagePullSecretManager(_ops.Object, _settings);

        var registries = manager.GetAllRegistries();

        registries.Count.ShouldBe(1);
        registries[0].Server.ShouldBe("registry.example.com");
    }

    [Fact]
    public void GetAllRegistries_MultipleRegistries_CombinesBoth()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "squid-ns",
            ScriptPodRegistryServer = "docker.io",
            ScriptPodRegistryUsername = "user1",
            ScriptPodRegistryPassword = "pass1",
            ScriptPodAdditionalRegistries = "[{\"server\":\"gcr.io\",\"username\":\"user2\",\"password\":\"pass2\"}]"
        };
        var manager = new ImagePullSecretManager(_ops.Object, settings);

        var registries = manager.GetAllRegistries();

        registries.Count.ShouldBe(2);
        registries[0].Server.ShouldBe("docker.io");
        registries[1].Server.ShouldBe("gcr.io");
    }

    [Fact]
    public void EnsureAllPullSecrets_MultipleRegistries_CreatesMultipleSecrets()
    {
        var settings = new KubernetesSettings
        {
            TentacleNamespace = "squid-ns",
            ScriptPodRegistryServer = "docker.io",
            ScriptPodRegistryUsername = "user1",
            ScriptPodRegistryPassword = "pass1",
            ScriptPodAdditionalRegistries = "[{\"server\":\"gcr.io\",\"username\":\"user2\",\"password\":\"pass2\"}]"
        };

        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);

        var manager = new ImagePullSecretManager(_ops.Object, settings);
        var secrets = manager.EnsureAllPullSecrets();

        secrets.Count.ShouldBe(2);
        _ops.Verify(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()), Times.Exactly(2));
    }

    [Fact]
    public void EnsureAllPullSecrets_CalledTwice_CreatesOnce()
    {
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);

        var manager = new ImagePullSecretManager(_ops.Object, _settings);
        manager.EnsureAllPullSecrets();
        manager.EnsureAllPullSecrets();

        _ops.Verify(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()), Times.Once);
    }

    // ========================================================================
    // Integration — pod spec includes dynamic secret
    // ========================================================================

    [Fact]
    public void CreatePod_WithPullSecretManager_IncludesDynamicSecret()
    {
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);

        V1Pod captured = null;

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var podSettings = new KubernetesSettings
        {
            TentacleNamespace = "squid-ns",
            ScriptPodServiceAccount = "sa",
            ScriptPodImage = "image:v1",
            ScriptPodTimeoutSeconds = 1800,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "squid-workspace",
            ScriptPodRegistryServer = "registry.example.com",
            ScriptPodRegistryUsername = "user",
            ScriptPodRegistryPassword = "pass"
        };

        var manager = new ImagePullSecretManager(_ops.Object, podSettings);
        var podMgr = new KubernetesPodManager(_ops.Object, podSettings, pullSecretManager: manager);
        podMgr.CreatePod("abcdef123456789000");

        captured.ShouldNotBeNull();
        captured.Spec.ImagePullSecrets.ShouldNotBeNull();
        captured.Spec.ImagePullSecrets.Count.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public void CreatePod_WithStaticAndDynamicSecrets_IncludesBoth()
    {
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);

        V1Pod captured = null;

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var podSettings = new KubernetesSettings
        {
            TentacleNamespace = "squid-ns",
            ScriptPodServiceAccount = "sa",
            ScriptPodImage = "image:v1",
            ScriptPodTimeoutSeconds = 1800,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "squid-workspace",
            ScriptPodImagePullSecrets = "static-secret",
            ScriptPodRegistryServer = "registry.example.com",
            ScriptPodRegistryUsername = "user",
            ScriptPodRegistryPassword = "pass"
        };

        var manager = new ImagePullSecretManager(_ops.Object, podSettings);
        var podMgr = new KubernetesPodManager(_ops.Object, podSettings, pullSecretManager: manager);
        podMgr.CreatePod("abcdef123456789000");

        captured.ShouldNotBeNull();
        captured.Spec.ImagePullSecrets.Count.ShouldBe(2);
        captured.Spec.ImagePullSecrets[0].Name.ShouldBe("static-secret");
    }

    [Fact]
    public void CreatePod_MultipleRegistries_PodHasAllPullSecrets()
    {
        _ops.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);

        V1Pod captured = null;

        _ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var podSettings = new KubernetesSettings
        {
            TentacleNamespace = "squid-ns",
            ScriptPodServiceAccount = "sa",
            ScriptPodImage = "image:v1",
            ScriptPodTimeoutSeconds = 1800,
            ScriptPodCpuRequest = "25m",
            ScriptPodMemoryRequest = "100Mi",
            ScriptPodCpuLimit = "500m",
            ScriptPodMemoryLimit = "512Mi",
            PvcClaimName = "squid-workspace",
            ScriptPodRegistryServer = "docker.io",
            ScriptPodRegistryUsername = "user1",
            ScriptPodRegistryPassword = "pass1",
            ScriptPodAdditionalRegistries = "[{\"server\":\"gcr.io\",\"username\":\"user2\",\"password\":\"pass2\"}]"
        };

        var manager = new ImagePullSecretManager(_ops.Object, podSettings);
        var podMgr = new KubernetesPodManager(_ops.Object, podSettings, pullSecretManager: manager);
        podMgr.CreatePod("abcdef123456789000");

        captured.ShouldNotBeNull();
        captured.Spec.ImagePullSecrets.Count.ShouldBe(2);
    }
}
