using System.Collections.Generic;
using System.Text.Json;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Health;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Health;

[Collection(TentacleMetricsCollection.Name)]
public class MetricsPersistenceTests
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();
    private readonly KubernetesSettings _settings = new() { TentacleNamespace = "squid-ns" };

    [Fact]
    public void Save_CreatesConfigMapWithMetrics()
    {
        TentacleMetrics.Reset();
        TentacleMetrics.ScriptStarted();
        TentacleMetrics.ScriptCompleted();

        V1ConfigMap captured = null;
        _ops.Setup(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), "squid-ns"))
            .Callback<V1ConfigMap, string>((cm, ns) => captured = cm)
            .Returns((V1ConfigMap cm, string ns) => cm);

        var persistence = new MetricsPersistence(_ops.Object, _settings);
        persistence.Save();

        captured.ShouldNotBeNull();
        captured.Metadata.Name.ShouldBe("squid-tentacle-metrics");
        captured.Data.ShouldContainKey("metrics");

        var snapshot = JsonSerializer.Deserialize<MetricsSnapshot>(captured.Data["metrics"]);
        snapshot.ScriptsStartedTotal.ShouldBe(1);
        snapshot.ScriptsCompletedTotal.ShouldBe(1);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void Restore_SetsMetricsFromConfigMap()
    {
        TentacleMetrics.Reset();

        var snapshot = new MetricsSnapshot
        {
            ScriptsStartedTotal = 100,
            ScriptsCompletedTotal = 90,
            ScriptsFailedTotal = 5,
            ScriptsCanceledTotal = 3,
            OrphanedPodsCleanedTotal = 2
        };

        var cm = new V1ConfigMap
        {
            Metadata = new V1ObjectMeta { Name = "squid-tentacle-metrics" },
            Data = new Dictionary<string, string> { ["metrics"] = JsonSerializer.Serialize(snapshot) }
        };

        _ops.Setup(o => o.ListConfigMaps("squid-ns", It.IsAny<string>()))
            .Returns(new V1ConfigMapList { Items = new List<V1ConfigMap> { cm } });

        var persistence = new MetricsPersistence(_ops.Object, _settings);
        persistence.Restore();

        TentacleMetrics.ScriptsStartedTotal.ShouldBe(100);
        TentacleMetrics.ScriptsCompletedTotal.ShouldBe(90);
        TentacleMetrics.ScriptsFailedTotal.ShouldBe(5);
        TentacleMetrics.ScriptsCanceledTotal.ShouldBe(3);
        TentacleMetrics.OrphanedPodsCleanedTotal.ShouldBe(2);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void Restore_NoConfigMap_DoesNotThrow()
    {
        TentacleMetrics.Reset();

        _ops.Setup(o => o.ListConfigMaps("squid-ns", It.IsAny<string>()))
            .Returns(new V1ConfigMapList { Items = new List<V1ConfigMap>() });

        var persistence = new MetricsPersistence(_ops.Object, _settings);
        persistence.Restore();

        TentacleMetrics.ScriptsStartedTotal.ShouldBe(0);

        TentacleMetrics.Reset();
    }

    [Fact]
    public void SaveIfDue_NotDue_SkipsSave()
    {
        var persistence = new MetricsPersistence(_ops.Object, _settings);
        persistence.Save(); // sets _lastSave

        persistence.SaveIfDue();

        _ops.Verify(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public void Save_ApiFailure_DoesNotThrow()
    {
        _ops.Setup(o => o.CreateOrReplaceConfigMap(It.IsAny<V1ConfigMap>(), It.IsAny<string>()))
            .Throws(new Exception("K8s API unavailable"));

        var persistence = new MetricsPersistence(_ops.Object, _settings);

        Should.NotThrow(() => persistence.Save());
    }
}
