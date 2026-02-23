using System.Collections.Generic;
using System.Linq;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.UnitTests.Services.Tentacle;

public class KubernetesPodManagerPodSpecTests
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
        PvcClaimName = "squid-workspace"
    };

    private const string TicketId = "abcdef123456789000";

    private V1Pod CaptureCreatedPod()
    {
        V1Pod captured = null;
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var manager = new KubernetesPodManager(ops.Object, _settings);
        manager.CreatePod(TicketId);

        return captured;
    }

    [Fact]
    public void CreatePod_GeneratesCorrectPodName()
    {
        var pod = CaptureCreatedPod();

        pod.Metadata.Name.ShouldBe("squid-script-abcdef123456");
    }

    [Fact]
    public void CreatePod_SetsCorrectLabels()
    {
        var pod = CaptureCreatedPod();

        pod.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "squid-tentacle");
        pod.Metadata.Labels.ShouldContainKeyAndValue("squid.io/ticket-id", TicketId);
    }

    [Fact]
    public void CreatePod_SetsCorrectNamespace()
    {
        var pod = CaptureCreatedPod();

        pod.Metadata.NamespaceProperty.ShouldBe("squid-ns");
    }

    [Fact]
    public void CreatePod_SetsServiceAccountName()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.ServiceAccountName.ShouldBe("squid-script-sa");
    }

    [Fact]
    public void CreatePod_SetsNeverRestartPolicy()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.RestartPolicy.ShouldBe("Never");
    }

    [Fact]
    public void CreatePod_SetsActiveDeadlineFromSettings()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.ActiveDeadlineSeconds.ShouldBe(1800);
    }

    [Fact]
    public void CreatePod_SetsContainerImageAndCommand()
    {
        var pod = CaptureCreatedPod();

        var container = pod.Spec.Containers.ShouldHaveSingleItem();
        container.Name.ShouldBe("script");
        container.Image.ShouldBe("bitnami/kubectl:1.28");
        container.Command.ShouldBe(new[] { "squid-calamari" });
        container.Args.ShouldBe(new[]
        {
            "run-script",
            $"--script=/squid/work/{TicketId}/script.sh",
            $"--variables=/squid/work/{TicketId}/variables.json"
        });
        container.WorkingDir.ShouldBe($"/squid/work/{TicketId}");
    }

    [Fact]
    public void CreatePod_SetsResourceRequestsAndLimits()
    {
        var pod = CaptureCreatedPod();

        var resources = pod.Spec.Containers[0].Resources;
        resources.Requests["cpu"].ToString().ShouldBe("25m");
        resources.Requests["memory"].ToString().ShouldBe("100Mi");
        resources.Limits["cpu"].ToString().ShouldBe("500m");
        resources.Limits["memory"].ToString().ShouldBe("512Mi");
    }

    [Fact]
    public void CreatePod_MountsWorkspacePvc()
    {
        var pod = CaptureCreatedPod();

        var volume = pod.Spec.Volumes.ShouldHaveSingleItem();
        volume.Name.ShouldBe("workspace");
        volume.PersistentVolumeClaim.ClaimName.ShouldBe("squid-workspace");

        var mount = pod.Spec.Containers[0].VolumeMounts.ShouldHaveSingleItem();
        mount.Name.ShouldBe("workspace");
        mount.MountPath.ShouldBe("/squid/work");
    }
}
