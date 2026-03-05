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

        pod.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
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
        container.Command.ShouldBe(new[] { "/squid/bin/squid-calamari" });
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

        var workspaceVolume = pod.Spec.Volumes.First(v => v.Name == "workspace");
        workspaceVolume.PersistentVolumeClaim.ClaimName.ShouldBe("squid-workspace");

        var workspaceMount = pod.Spec.Containers[0].VolumeMounts.First(m => m.Name == "workspace");
        workspaceMount.MountPath.ShouldBe("/squid/work");
    }

    // ========================================================================
    // Init Container — TentacleImage set
    // ========================================================================

    [Fact]
    public void CreatePod_WithTentacleImage_AddsInitContainer()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        var initContainer = pod.Spec.InitContainers.ShouldHaveSingleItem();
        initContainer.Name.ShouldBe("copy-calamari");
        initContainer.Image.ShouldBe("squidcd/squid-tentacle:1.0.0");
        initContainer.Command.ShouldBe(new[] { "cp", "/squid/bin/squid-calamari", "/squid-bin/squid-calamari" });
    }

    [Fact]
    public void CreatePod_WithTentacleImage_AddsSquidBinEmptyDirVolume()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        pod.Spec.Volumes.Count.ShouldBe(2);
        var squidBinVolume = pod.Spec.Volumes.First(v => v.Name == "squid-bin");
        squidBinVolume.EmptyDir.ShouldNotBeNull();
    }

    [Fact]
    public void CreatePod_WithTentacleImage_MountsSquidBinInMainContainer()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        var mounts = pod.Spec.Containers[0].VolumeMounts;
        mounts.Count.ShouldBe(2);

        var squidBinMount = mounts.First(m => m.Name == "squid-bin");
        squidBinMount.MountPath.ShouldBe("/squid/bin");
    }

    [Fact]
    public void CreatePod_WithTentacleImage_InitContainerMountsSquidBin()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        var initMount = pod.Spec.InitContainers[0].VolumeMounts.ShouldHaveSingleItem();
        initMount.Name.ShouldBe("squid-bin");
        initMount.MountPath.ShouldBe("/squid-bin");
    }

    [Fact]
    public void CreatePod_SetsRunAsUserZero()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.SecurityContext.ShouldNotBeNull();
        pod.Spec.SecurityContext.RunAsUser.ShouldBe(0);
    }

    [Fact]
    public void CreatePod_SetsImagePullPolicyIfNotPresent()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Containers[0].ImagePullPolicy.ShouldBe("IfNotPresent");
    }

    // ========================================================================
    // Backward Compatibility — TentacleImage empty
    // ========================================================================

    [Fact]
    public void CreatePod_WithoutTentacleImage_HasNoInitContainers()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.InitContainers.ShouldBeNull();
    }

    [Fact]
    public void CreatePod_WithoutTentacleImage_HasSingleVolume()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Volumes.ShouldHaveSingleItem().Name.ShouldBe("workspace");
    }

    [Fact]
    public void CreatePod_WithoutTentacleImage_HasSingleVolumeMount()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Containers[0].VolumeMounts.ShouldHaveSingleItem().Name.ShouldBe("workspace");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private V1Pod CaptureCreatedPodWithTentacleImage(string tentacleImage)
    {
        V1Pod captured = null;
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var settingsWithImage = new KubernetesSettings
        {
            TentacleNamespace = _settings.TentacleNamespace,
            ScriptPodServiceAccount = _settings.ScriptPodServiceAccount,
            ScriptPodImage = _settings.ScriptPodImage,
            ScriptPodTimeoutSeconds = _settings.ScriptPodTimeoutSeconds,
            ScriptPodCpuRequest = _settings.ScriptPodCpuRequest,
            ScriptPodMemoryRequest = _settings.ScriptPodMemoryRequest,
            ScriptPodCpuLimit = _settings.ScriptPodCpuLimit,
            ScriptPodMemoryLimit = _settings.ScriptPodMemoryLimit,
            PvcClaimName = _settings.PvcClaimName,
            TentacleImage = tentacleImage
        };

        var manager = new KubernetesPodManager(ops.Object, settingsWithImage);
        manager.CreatePod(TicketId);

        return captured;
    }
}
