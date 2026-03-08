using System;
using System.Collections.Generic;
using System.Text;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesResourceManagerTests
{
    private readonly Mock<IKubernetesPodOperations> _podOps = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "squid-ns" };
    private readonly KubernetesResourceManager _manager;

    public KubernetesResourceManagerTests()
    {
        _podOps.Setup(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), It.IsAny<string>()))
            .Returns((V1ConfigMap cm, string ns) => cm);
        _podOps.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Returns((V1Secret s, string ns) => s);
        _manager = new KubernetesResourceManager(_podOps.Object, _settings);
    }

    // ========================================================================
    // ConfigMap operations
    // ========================================================================

    [Fact]
    public void CreateOrUpdateConfigMap_PassesCorrectNamespace()
    {
        _manager.CreateOrUpdateConfigMap("test-cm", new Dictionary<string, string> { ["key"] = "value" });

        _podOps.Verify(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), "squid-ns"), Times.Once);
    }

    [Fact]
    public void CreateOrUpdateConfigMap_SetsData()
    {
        V1ConfigMap captured = null;
        _podOps.Setup(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), It.IsAny<string>()))
            .Callback<V1ConfigMap, string>((cm, _) => captured = cm)
            .Returns((V1ConfigMap cm, string _) => cm);

        _manager.CreateOrUpdateConfigMap("test-cm", new Dictionary<string, string> { ["env"] = "production" });

        captured.ShouldNotBeNull();
        captured.Data.ShouldContainKeyAndValue("env", "production");
    }

    [Fact]
    public void CreateOrUpdateConfigMap_SetsManagedByLabel()
    {
        V1ConfigMap captured = null;
        _podOps.Setup(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), It.IsAny<string>()))
            .Callback<V1ConfigMap, string>((cm, _) => captured = cm)
            .Returns((V1ConfigMap cm, string _) => cm);

        _manager.CreateOrUpdateConfigMap("test-cm", new Dictionary<string, string>());

        captured.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
    }

    [Fact]
    public void CreateOrUpdateConfigMap_ReturnsResult()
    {
        var result = _manager.CreateOrUpdateConfigMap("test-cm", new Dictionary<string, string>());

        result.ShouldNotBeNull();
        result.Metadata.Name.ShouldBe("test-cm");
    }

    // ========================================================================
    // Secret operations
    // ========================================================================

    [Fact]
    public void CreateOrUpdateSecret_EncodesDataAsBytes()
    {
        V1Secret captured = null;
        _podOps.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Callback<V1Secret, string>((s, _) => captured = s)
            .Returns((V1Secret s, string _) => s);

        _manager.CreateOrUpdateSecret("test-secret", new Dictionary<string, string> { ["password"] = "abc123" });

        captured.ShouldNotBeNull();
        captured.Data.ShouldContainKey("password");
        Encoding.UTF8.GetString(captured.Data["password"]).ShouldBe("abc123");
    }

    [Fact]
    public void CreateOrUpdateSecret_TypeIsOpaque()
    {
        V1Secret captured = null;
        _podOps.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Callback<V1Secret, string>((s, _) => captured = s)
            .Returns((V1Secret s, string _) => s);

        _manager.CreateOrUpdateSecret("test-secret", new Dictionary<string, string>());

        captured.Type.ShouldBe("Opaque");
    }

    [Fact]
    public void CreateOrUpdateSecret_SetsManagedByLabel()
    {
        V1Secret captured = null;
        _podOps.Setup(o => o.CreateOrReplaceSecret(It.IsAny<V1Secret>(), It.IsAny<string>()))
            .Callback<V1Secret, string>((s, _) => captured = s)
            .Returns((V1Secret s, string _) => s);

        _manager.CreateOrUpdateSecret("test-secret", new Dictionary<string, string>());

        captured.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
    }

    // ========================================================================
    // Delete operations — exception resilience
    // ========================================================================

    [Fact]
    public void DeleteConfigMap_CallsCorrectNamespace()
    {
        _manager.DeleteConfigMap("old-cm");

        _podOps.Verify(o => o.DeleteConfigMap("old-cm", "squid-ns"), Times.Once);
    }

    [Fact]
    public void DeleteConfigMap_ExceptionDoesNotPropagate()
    {
        _podOps.Setup(o => o.DeleteConfigMap(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new Exception("not found"));

        // Should not throw
        _manager.DeleteConfigMap("missing-cm");
    }

    [Fact]
    public void DeleteSecret_CallsCorrectNamespace()
    {
        _manager.DeleteSecret("old-secret");

        _podOps.Verify(o => o.DeleteSecret("old-secret", "squid-ns"), Times.Once);
    }

    [Fact]
    public void DeleteSecret_ExceptionDoesNotPropagate()
    {
        _podOps.Setup(o => o.DeleteSecret(It.IsAny<string>(), It.IsAny<string>()))
            .Throws(new Exception("not found"));

        // Should not throw
        _manager.DeleteSecret("missing-secret");
    }
}
