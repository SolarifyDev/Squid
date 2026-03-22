using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class PodDisruptionBudgetManagerTests
{
    private readonly Mock<IKubernetesPodOperations> _ops = new();

    private PodDisruptionBudgetManager CreateManager(string releaseName = "")
    {
        var settings = new KubernetesSettings { TentacleNamespace = "test-ns", ReleaseName = releaseName };
        return new PodDisruptionBudgetManager(_ops.Object, settings);
    }

    [Fact]
    public void NoPdbExists_CreatesPdb()
    {
        var manager = CreateManager();
        _ops.Setup(o => o.ReadPodDisruptionBudget("squid-script-pdb", "test-ns")).Returns((V1PodDisruptionBudget)null);

        manager.EnsurePdbExists();

        _ops.Verify(o => o.CreatePodDisruptionBudget(It.IsAny<V1PodDisruptionBudget>(), "test-ns"), Times.Once);
    }

    [Fact]
    public void PdbAlreadyExists_Skips()
    {
        var manager = CreateManager();
        _ops.Setup(o => o.ReadPodDisruptionBudget("squid-script-pdb", "test-ns")).Returns(new V1PodDisruptionBudget());

        manager.EnsurePdbExists();

        _ops.Verify(o => o.CreatePodDisruptionBudget(It.IsAny<V1PodDisruptionBudget>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void CorrectLabelSelector()
    {
        var manager = CreateManager();
        var selector = manager.BuildLabelSelector();

        selector.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
    }

    [Fact]
    public void MaxUnavailableIsZero()
    {
        V1PodDisruptionBudget captured = null;
        var manager = CreateManager();
        _ops.Setup(o => o.ReadPodDisruptionBudget(It.IsAny<string>(), "test-ns")).Returns((V1PodDisruptionBudget)null);
        _ops.Setup(o => o.CreatePodDisruptionBudget(It.IsAny<V1PodDisruptionBudget>(), "test-ns"))
            .Callback<V1PodDisruptionBudget, string>((pdb, ns) => captured = pdb)
            .Returns((V1PodDisruptionBudget pdb, string ns) => pdb);

        manager.EnsurePdbExists();

        captured.ShouldNotBeNull();
        captured.Spec.MaxUnavailable.Value.ShouldBe("0");
    }

    [Fact]
    public void WithReleaseName_IncludesInstanceLabel()
    {
        var manager = CreateManager("squid-prod");
        var selector = manager.BuildLabelSelector();

        selector.ShouldContainKeyAndValue("app.kubernetes.io/instance", "squid-prod");
    }

    [Fact]
    public void WithoutReleaseName_OmitsInstanceLabel()
    {
        var manager = CreateManager();
        var selector = manager.BuildLabelSelector();

        selector.ShouldNotContainKey("app.kubernetes.io/instance");
    }

    [Fact]
    public void WithReleaseName_PdbNameIncludesReleaseName()
    {
        var manager = CreateManager("squid-prod");

        manager.BuildPdbName().ShouldBe("squid-prod-squid-script-pdb");
    }

    [Fact]
    public void WithoutReleaseName_PdbNameIsDefault()
    {
        var manager = CreateManager();

        manager.BuildPdbName().ShouldBe("squid-script-pdb");
    }
}
