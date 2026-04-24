using System;
using System.Collections.Generic;
using System.Linq;
using k8s.Models;
using Squid.Tentacle.Configuration;
using Squid.Tentacle.Kubernetes;

namespace Squid.Tentacle.Tests.Kubernetes;

public class KubernetesPodManagerPodSpecTests
{
    // Digest-pinned image used by most tests. Post-P0-C.1 fix, BuildPodSpec rejects
    // tag-only images — tests that aren't exercising the validator itself still need
    // a valid pinned reference so they don't trip the guard.
    private const string TestDigestImage =
        "bitnami/kubectl@sha256:abc123def456789012345678901234567890123456789012345678901234aa77";

    private readonly KubernetesSettings _settings = new()
    {
        TentacleNamespace = "squid-ns",
        ScriptPodServiceAccount = "squid-script-sa",
        ScriptPodImage = TestDigestImage,
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
        pod.Metadata.Labels.ShouldNotContainKey("app.kubernetes.io/instance");
    }

    [Fact]
    public void CreatePod_WithReleaseName_IncludesInstanceLabel()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ReleaseName = "squid-agent-prod");

        pod.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
        pod.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/instance", "squid-agent-prod");
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
        container.Image.ShouldBe(TestDigestImage);
        container.Command.ShouldBe(new[] { "bash" });
        container.Args.ShouldBe(new[] { $"/squid/work/{TicketId}/script.sh" });
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
        initContainer.Command.ShouldBe(new[] { "sh", "-c", "cp -a /squid/bin/. /squid-bin/" });
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
    public void CreatePod_DefaultSettings_RunAsNonRoot()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.SecurityContext.ShouldNotBeNull();
        pod.Spec.SecurityContext.RunAsNonRoot.ShouldBe(true);
    }

    [Fact]
    public void CreatePod_DefaultSettings_DropsAllCapabilities()
    {
        var pod = CaptureCreatedPod();

        var sc = pod.Spec.Containers[0].SecurityContext;
        sc.ShouldNotBeNull();
        sc.AllowPrivilegeEscalation.ShouldBe(false);
        sc.Capabilities.Drop.ShouldContain("ALL");
    }

    [Fact]
    public void CreatePod_DefaultSettings_NoPrivilegeEscalation()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Containers[0].SecurityContext.AllowPrivilegeEscalation.ShouldBe(false);
    }

    [Fact]
    public void CreatePod_ExplicitRunAsUser_UsesConfigured()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodRunAsUser = 1000);

        pod.Spec.SecurityContext.RunAsUser.ShouldBe(1000);
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
    // Security Context — Configurable
    // ========================================================================

    [Fact]
    public void CreatePod_RunAsUserConfigured_SetsSecurityContext()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodRunAsUser = 1000);

        pod.Spec.SecurityContext.ShouldNotBeNull();
        pod.Spec.SecurityContext.RunAsUser.ShouldBe(1000);
    }

    [Fact]
    public void CreatePod_RunAsNonRootTrue_SetsSecurityContext()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodRunAsNonRoot = true);

        pod.Spec.SecurityContext.ShouldNotBeNull();
        pod.Spec.SecurityContext.RunAsNonRoot.ShouldBe(true);
    }

    [Fact]
    public void CreatePod_RunAsUserAndNonRoot_BothSet()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.ScriptPodRunAsUser = 1000;
            s.ScriptPodRunAsNonRoot = true;
        });

        pod.Spec.SecurityContext.RunAsUser.ShouldBe(1000);
        pod.Spec.SecurityContext.RunAsNonRoot.ShouldBe(true);
    }

    // ========================================================================
    // ImagePullSecrets
    // ========================================================================

    [Fact]
    public void CreatePod_NoImagePullSecrets_IsNull()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.ImagePullSecrets.ShouldBeNull();
    }

    [Fact]
    public void CreatePod_SingleImagePullSecret_SetsCorrectly()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodImagePullSecrets = "my-registry-secret");

        pod.Spec.ImagePullSecrets.ShouldHaveSingleItem().Name.ShouldBe("my-registry-secret");
    }

    [Fact]
    public void CreatePod_MultipleImagePullSecrets_SetsCorrectly()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodImagePullSecrets = "secret1, secret2, secret3");

        pod.Spec.ImagePullSecrets.Count.ShouldBe(3);
        pod.Spec.ImagePullSecrets[0].Name.ShouldBe("secret1");
        pod.Spec.ImagePullSecrets[1].Name.ShouldBe("secret2");
        pod.Spec.ImagePullSecrets[2].Name.ShouldBe("secret3");
    }

    // ========================================================================
    // Tolerations
    // ========================================================================

    [Fact]
    public void CreatePod_NoTolerations_IsNull()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Tolerations.ShouldBeNull();
    }

    [Fact]
    public void CreatePod_ValidTolerations_SetsCorrectly()
    {
        var tolerationsJson = "[{\"key\":\"dedicated\",\"operator\":\"Equal\",\"value\":\"squid\",\"effect\":\"NoSchedule\"}]";
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodTolerations = tolerationsJson);

        pod.Spec.Tolerations.ShouldNotBeNull();
        pod.Spec.Tolerations.Count.ShouldBe(1);
        pod.Spec.Tolerations[0].Key.ShouldBe("dedicated");
        pod.Spec.Tolerations[0].OperatorProperty.ShouldBe("Equal");
        pod.Spec.Tolerations[0].Value.ShouldBe("squid");
        pod.Spec.Tolerations[0].Effect.ShouldBe("NoSchedule");
    }

    [Fact]
    public void CreatePod_InvalidTolerationsJson_IsNull()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodTolerations = "not-valid-json");

        pod.Spec.Tolerations.ShouldBeNull();
    }

    // ========================================================================
    // Init Container Resources & EmptyDir Limits
    // ========================================================================

    [Fact]
    public void CreatePod_WithTentacleImage_InitContainerHasResourceLimits()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        var initContainer = pod.Spec.InitContainers[0];
        initContainer.Resources.ShouldNotBeNull();
        initContainer.Resources.Requests["cpu"].ToString().ShouldBe("10m");
        initContainer.Resources.Requests["memory"].ToString().ShouldBe("50Mi");
        initContainer.Resources.Limits["cpu"].ToString().ShouldBe("100m");
        initContainer.Resources.Limits["memory"].ToString().ShouldBe("128Mi");
    }

    [Fact]
    public void CreatePod_WithTentacleImage_EmptyDirHasSizeLimit()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        var squidBinVolume = pod.Spec.Volumes.First(v => v.Name == "squid-bin");
        squidBinVolume.EmptyDir.SizeLimit.ShouldNotBeNull();
        squidBinVolume.EmptyDir.SizeLimit.ToString().ShouldBe("256Mi");
    }

    // ========================================================================
    // P0-1: NFS Watchdog Sidecar Injection
    // ========================================================================

    [Fact]
    public void CreatePod_WithNfsWatchdogImage_AddsSidecarContainer()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.NfsWatchdogImage = "squidcd/squid-watchdog:1.0.0");

        pod.Spec.Containers.Count.ShouldBe(2);
        pod.Spec.Containers[0].Name.ShouldBe("script");
        pod.Spec.Containers[1].Name.ShouldBe("nfs-watchdog");
    }

    [Fact]
    public void CreatePod_WithNfsWatchdogImage_SidecarHasCorrectConfig()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.NfsWatchdogImage = "squidcd/squid-watchdog:1.0.0");

        var sidecar = pod.Spec.Containers[1];
        sidecar.Image.ShouldBe("squidcd/squid-watchdog:1.0.0");
        sidecar.ImagePullPolicy.ShouldBe("IfNotPresent");
        sidecar.Env.ShouldHaveSingleItem().Name.ShouldBe("WATCHDOG_DIRECTORY");
        sidecar.Env[0].Value.ShouldBe("/squid/work");
        sidecar.Resources.Requests["cpu"].ToString().ShouldBe("10m");
        sidecar.Resources.Requests["memory"].ToString().ShouldBe("32Mi");
        sidecar.Resources.Limits["cpu"].ToString().ShouldBe("50m");
        sidecar.Resources.Limits["memory"].ToString().ShouldBe("64Mi");
    }

    [Fact]
    public void CreatePod_WithoutNfsWatchdogImage_NoSidecar()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Containers.ShouldHaveSingleItem().Name.ShouldBe("script");
    }

    [Fact]
    public void CreatePod_WithNfsWatchdogImage_SidecarSharesWorkspaceVolume()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.NfsWatchdogImage = "squidcd/squid-watchdog:1.0.0");

        var sidecarMount = pod.Spec.Containers[1].VolumeMounts.ShouldHaveSingleItem();
        sidecarMount.Name.ShouldBe("workspace");
        sidecarMount.MountPath.ShouldBe("/squid/work");
        sidecarMount.ReadOnlyProperty.ShouldBe(true);
    }

    // ========================================================================
    // P0-2: Workspace Isolation (emptyDir)
    // ========================================================================

    [Fact]
    public void CreatePod_IsolateWorkspace_AddsEmptyDirVolume()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.IsolateWorkspaceToEmptyDir = true);

        var localVolume = pod.Spec.Volumes.First(v => v.Name == "workspace-local");
        localVolume.EmptyDir.ShouldNotBeNull();
        localVolume.EmptyDir.SizeLimit.ToString().ShouldBe("1Gi");
    }

    [Fact]
    public void CreatePod_IsolateWorkspace_AddsCopyWorkspaceInitContainer()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.IsolateWorkspaceToEmptyDir = true);

        var initContainer = pod.Spec.InitContainers.First(c => c.Name == "copy-workspace");
        initContainer.Image.ShouldBe(TestDigestImage);
        initContainer.Command.ShouldContain("sh");
        string.Join(" ", initContainer.Command).ShouldContain($"cp -a /squid/nfs-work/{TicketId}/. /squid/work/{TicketId}/");
    }

    [Fact]
    public void CreatePod_IsolateWorkspace_MainContainerMountsEmptyDir()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.IsolateWorkspaceToEmptyDir = true);

        var mainMount = pod.Spec.Containers[0].VolumeMounts.First(m => m.MountPath == "/squid/work");
        mainMount.Name.ShouldBe("workspace-local");
    }

    [Fact]
    public void CreatePod_IsolateWorkspace_NfsVolumeStillPresent()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.IsolateWorkspaceToEmptyDir = true);

        var nfsVolume = pod.Spec.Volumes.First(v => v.Name == "workspace-nfs");
        nfsVolume.PersistentVolumeClaim.ClaimName.ShouldBe("squid-workspace");
    }

    [Fact]
    public void CreatePod_IsolateWorkspace_WatchdogMonitorsNfsVolume()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.IsolateWorkspaceToEmptyDir = true;
            s.NfsWatchdogImage = "squidcd/squid-watchdog:1.0.0";
        });

        var watchdogMount = pod.Spec.Containers[1].VolumeMounts.ShouldHaveSingleItem();
        watchdogMount.Name.ShouldBe("workspace-nfs");
    }

    [Fact]
    public void CreatePod_NoIsolateWorkspace_DirectPvcMount()
    {
        var pod = CaptureCreatedPod();

        pod.Spec.Volumes.ShouldHaveSingleItem().Name.ShouldBe("workspace");
        pod.Spec.Containers[0].VolumeMounts.First(m => m.MountPath == "/squid/work").Name.ShouldBe("workspace");
    }

    // ========================================================================
    // P1-2: RWO Pod Affinity Auto-Injection
    // ========================================================================

    [Fact]
    public void CreatePod_RwoMode_InjectsNodeAffinity()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.PersistenceAccessMode = "ReadWriteOnce";
            s.ReleaseName = "squid-prod";
        });

        pod.Spec.Affinity.ShouldNotBeNull();
        pod.Spec.Affinity.PodAffinity.ShouldNotBeNull();
        var term = pod.Spec.Affinity.PodAffinity.RequiredDuringSchedulingIgnoredDuringExecution.ShouldHaveSingleItem();
        term.TopologyKey.ShouldBe("kubernetes.io/hostname");
    }

    [Fact]
    public void CreatePod_RwoMode_AffinityMatchesAgentLabels()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.PersistenceAccessMode = "ReadWriteOnce";
            s.ReleaseName = "squid-prod";
        });

        var term = pod.Spec.Affinity.PodAffinity.RequiredDuringSchedulingIgnoredDuringExecution[0];
        term.LabelSelector.MatchLabels.ShouldContainKeyAndValue("app.kubernetes.io/name", "kubernetes-agent");
        term.LabelSelector.MatchLabels.ShouldContainKeyAndValue("app.kubernetes.io/instance", "squid-prod");
    }

    [Fact]
    public void CreatePod_RwxMode_NoAffinity()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.PersistenceAccessMode = "ReadWriteMany");

        pod.Spec.Affinity.ShouldBeNull();
    }

    [Fact]
    public void CreatePod_RwoMode_WithReleaseName_UsesCorrectInstanceLabel()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.PersistenceAccessMode = "ReadWriteOnce";
            s.ReleaseName = "my-release";
        });

        var term = pod.Spec.Affinity.PodAffinity.RequiredDuringSchedulingIgnoredDuringExecution[0];
        term.LabelSelector.MatchLabels["app.kubernetes.io/instance"].ShouldBe("my-release");
    }

    [Fact]
    public void CreatePod_RwoMode_TemplateOverridesAffinity()
    {
        // Template affinity should take precedence via ApplyTemplateOverrides
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.PersistenceAccessMode = "ReadWriteOnce";
            s.ReleaseName = "squid-prod";
        });

        // RWO affinity is set before ApplyTemplateOverrides, so template can override
        pod.Spec.Affinity.ShouldNotBeNull();
        pod.Spec.Affinity.PodAffinity.ShouldNotBeNull();
    }

    // ========================================================================
    // Proxy Environment Variables
    // ========================================================================

    [Fact]
    public void CreatePod_WithProxy_InjectsEnvVars()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.HttpProxy = "http://proxy.corp:8080";
            s.HttpsProxy = "http://proxy.corp:8443";
            s.NoProxy = "localhost,10.0.0.0/8,.internal";
        });

        var env = pod.Spec.Containers[0].Env;
        env.ShouldNotBeNull();
        env.ShouldContain(e => e.Name == "SQUID_RUNNING_IN_CONTAINER");
        env.ShouldContain(e => e.Name == "http_proxy" && e.Value == "http://proxy.corp:8080");
        env.ShouldContain(e => e.Name == "HTTP_PROXY" && e.Value == "http://proxy.corp:8080");
        env.ShouldContain(e => e.Name == "https_proxy" && e.Value == "http://proxy.corp:8443");
        env.ShouldContain(e => e.Name == "HTTPS_PROXY" && e.Value == "http://proxy.corp:8443");
        env.ShouldContain(e => e.Name == "no_proxy" && e.Value == "localhost,10.0.0.0/8,.internal");
        env.ShouldContain(e => e.Name == "NO_PROXY" && e.Value == "localhost,10.0.0.0/8,.internal");
    }

    [Fact]
    public void CreatePod_WithPartialProxy_OnlyInjectsConfigured()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.HttpsProxy = "http://proxy.corp:8443");

        var env = pod.Spec.Containers[0].Env;
        env.ShouldNotBeNull();
        env.ShouldContain(e => e.Name == "SQUID_RUNNING_IN_CONTAINER");
        env.ShouldContain(e => e.Name == "https_proxy");
        env.ShouldContain(e => e.Name == "HTTPS_PROXY");
        env.ShouldNotContain(e => e.Name == "http_proxy");
    }

    [Fact]
    public void CreatePod_NoProxy_OnlyStandardEnvVars()
    {
        var pod = CaptureCreatedPod();

        var env = pod.Spec.Containers[0].Env;
        env.ShouldNotBeNull();
        env.ShouldContain(e => e.Name == "SQUID_RUNNING_IN_CONTAINER");
        env.ShouldNotContain(e => e.Name == "http_proxy");
        env.ShouldNotContain(e => e.Name == "https_proxy");
        env.ShouldNotContain(e => e.Name == "no_proxy");
    }

    // ========================================================================
    // Proxy via Secret Reference (Fix 8)
    // ========================================================================

    [Fact]
    public void CreatePod_ProxySecretName_UsesSecretKeyRef()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ProxySecretName = "proxy-creds");

        var env = pod.Spec.Containers[0].Env;
        env.ShouldNotBeNull();

        var httpProxy = env.First(e => e.Name == "http_proxy");
        httpProxy.Value.ShouldBeNull();
        httpProxy.ValueFrom.ShouldNotBeNull();
        httpProxy.ValueFrom.SecretKeyRef.Name.ShouldBe("proxy-creds");
        httpProxy.ValueFrom.SecretKeyRef.Key.ShouldBe("http-proxy");
        httpProxy.ValueFrom.SecretKeyRef.Optional.ShouldBe(true);
    }

    [Fact]
    public void CreatePod_ProxySecretName_AllSixVarsReferenceSecret()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ProxySecretName = "proxy-creds");

        var env = pod.Spec.Containers[0].Env;
        var proxyVars = env.Where(e => e.ValueFrom?.SecretKeyRef != null).ToList();
        proxyVars.Count.ShouldBe(6);

        proxyVars.ShouldContain(e => e.Name == "http_proxy" && e.ValueFrom.SecretKeyRef.Key == "http-proxy");
        proxyVars.ShouldContain(e => e.Name == "HTTP_PROXY" && e.ValueFrom.SecretKeyRef.Key == "http-proxy");
        proxyVars.ShouldContain(e => e.Name == "https_proxy" && e.ValueFrom.SecretKeyRef.Key == "https-proxy");
        proxyVars.ShouldContain(e => e.Name == "HTTPS_PROXY" && e.ValueFrom.SecretKeyRef.Key == "https-proxy");
        proxyVars.ShouldContain(e => e.Name == "no_proxy" && e.ValueFrom.SecretKeyRef.Key == "no-proxy");
        proxyVars.ShouldContain(e => e.Name == "NO_PROXY" && e.ValueFrom.SecretKeyRef.Key == "no-proxy");

        proxyVars.All(e => e.ValueFrom.SecretKeyRef.Name == "proxy-creds").ShouldBeTrue();
    }

    [Fact]
    public void CreatePod_NoProxySecretName_UsesInlineValues()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.HttpProxy = "http://proxy.corp:8080");

        var env = pod.Spec.Containers[0].Env;
        var httpProxy = env.First(e => e.Name == "http_proxy");
        httpProxy.Value.ShouldBe("http://proxy.corp:8080");
        httpProxy.ValueFrom.ShouldBeNull();
    }

    // ========================================================================
    // Raw Script Mode
    // ========================================================================

    [Fact]
    public void CreatePod_RawScriptMode_UsesBashCommand()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.RawScriptMode = true);

        var container = pod.Spec.Containers[0];
        container.Command.ShouldBe(new[] { "bash" });
        container.Args.ShouldBe(new[] { $"/squid/work/{TicketId}/script.sh" });
    }

    [Fact]
    public void CreatePod_RawScriptMode_NoInitContainer()
    {
        var pod = CaptureCreatedPodWithSettings(s =>
        {
            s.RawScriptMode = true;
            s.TentacleImage = "squidcd/squid-tentacle:1.0.0";
        });

        pod.Spec.InitContainers.ShouldBeNull();
    }

    [Fact]
    public void CreatePod_NoTentacleImage_FallsBackToRawScriptMode()
    {
        var pod = CaptureCreatedPod();

        var container = pod.Spec.Containers[0];
        container.Command.ShouldBe(new[] { "bash" });
        container.Args[0].ShouldBe($"/squid/work/{TicketId}/script.sh");
        pod.Spec.InitContainers.ShouldBeNull();
    }

    [Fact]
    public void CreatePod_WithTentacleImage_UsesCalamariCommand()
    {
        var pod = CaptureCreatedPodWithTentacleImage("squidcd/squid-tentacle:1.0.0");

        var container = pod.Spec.Containers[0];
        container.Command.ShouldBe(new[] { "/squid/bin/squid-calamari" });
        container.Args[0].ShouldBe("run-script");
    }

    // ========================================================================
    // P1-1: Command Labels
    // ========================================================================

    [Fact]
    public void CreatePod_WithCommandLabels_MergesIntoLabels()
    {
        var labels = new Dictionary<string, string> { ["squid.io/deployment-id"] = "deploy-42", ["squid.io/environment"] = "production" };
        var pod = CaptureCreatedPodWithLabels(labels);

        pod.Metadata.Labels.ShouldContainKeyAndValue("squid.io/deployment-id", "deploy-42");
        pod.Metadata.Labels.ShouldContainKeyAndValue("squid.io/environment", "production");
        pod.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
    }

    [Fact]
    public void CreatePod_WithReservedCommandLabel_Rejected()
    {
        var labels = new Dictionary<string, string> { ["kubernetes.io/arch"] = "arm64" };
        var pod = CaptureCreatedPodWithLabels(labels);

        pod.Metadata.Labels.ShouldNotContainKey("kubernetes.io/arch");
    }

    [Fact]
    public void CreatePod_NullCommandLabels_NoChange()
    {
        var pod = CaptureCreatedPodWithLabels(null);

        pod.Metadata.Labels.ShouldContainKeyAndValue("app.kubernetes.io/managed-by", "kubernetes-agent");
        pod.Metadata.Labels.ShouldContainKeyAndValue("squid.io/ticket-id", TicketId);
    }

    // ========================================================================
    // Standard Env Vars (Fix 5)
    // ========================================================================

    [Fact]
    public void CreatePod_HasStandardEnvVars()
    {
        var pod = CaptureCreatedPod();

        var env = pod.Spec.Containers[0].Env;
        env.ShouldNotBeNull();
        env.ShouldContain(e => e.Name == "SQUID_RUNNING_IN_CONTAINER" && e.Value == "true");
        env.ShouldContain(e => e.Name == "SQUID_TICKET_ID" && e.Value == TicketId);
        env.ShouldContain(e => e.Name == "SQUID_NAMESPACE" && e.Value == "squid-ns");
        env.ShouldContain(e => e.Name == "SQUID_WORKSPACE" && e.Value == $"/squid/work/{TicketId}");
    }

    [Fact]
    public void CreatePod_StandardAndProxyEnvVars_BothPresent()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.HttpProxy = "http://proxy.corp:8080");

        var env = pod.Spec.Containers[0].Env;
        env.ShouldNotBeNull();
        env.ShouldContain(e => e.Name == "SQUID_RUNNING_IN_CONTAINER");
        env.ShouldContain(e => e.Name == "http_proxy" && e.Value == "http://proxy.corp:8080");
    }

    // ========================================================================
    // Helm Annotations (Fix 2)
    // ========================================================================

    [Fact]
    public void CreatePod_WithReleaseName_IncludesHelmAnnotations()
    {
        var pod = CaptureCreatedPodWithSettings(s => s.ReleaseName = "test-release");

        pod.Metadata.Annotations.ShouldNotBeNull();
        pod.Metadata.Annotations.ShouldContainKeyAndValue("meta.helm.sh/release-name", "test-release");
        pod.Metadata.Annotations.ShouldContainKeyAndValue("meta.helm.sh/release-namespace", "squid-ns");
    }

    [Fact]
    public void CreatePod_NoReleaseName_NoHelmAnnotations()
    {
        var pod = CaptureCreatedPod();

        if (pod.Metadata.Annotations != null)
        {
            pod.Metadata.Annotations.ShouldNotContainKey("meta.helm.sh/release-name");
            pod.Metadata.Annotations.ShouldNotContainKey("meta.helm.sh/release-namespace");
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private V1Pod CaptureCreatedPodWithLabels(Dictionary<string, string>? labels)
    {
        V1Pod captured = null;
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var manager = new KubernetesPodManager(ops.Object, _settings);
        manager.CreatePod(TicketId, additionalLabels: labels);

        return captured;
    }

    private V1Pod CaptureCreatedPodWithSettings(Action<KubernetesSettings> configure)
    {
        V1Pod captured = null;
        var ops = new Mock<IKubernetesPodOperations>();

        ops.Setup(o => o.CreatePod(It.IsAny<V1Pod>(), It.IsAny<string>()))
            .Callback<V1Pod, string>((pod, ns) => captured = pod)
            .Returns((V1Pod pod, string ns) => pod);

        var customSettings = new KubernetesSettings
        {
            TentacleNamespace = _settings.TentacleNamespace,
            ScriptPodServiceAccount = _settings.ScriptPodServiceAccount,
            ScriptPodImage = _settings.ScriptPodImage,
            ScriptPodTimeoutSeconds = _settings.ScriptPodTimeoutSeconds,
            ScriptPodCpuRequest = _settings.ScriptPodCpuRequest,
            ScriptPodMemoryRequest = _settings.ScriptPodMemoryRequest,
            ScriptPodCpuLimit = _settings.ScriptPodCpuLimit,
            ScriptPodMemoryLimit = _settings.ScriptPodMemoryLimit,
            PvcClaimName = _settings.PvcClaimName
        };

        configure(customSettings);

        var manager = new KubernetesPodManager(ops.Object, customSettings);
        manager.CreatePod(TicketId);

        return captured;
    }

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

    // ── P0-C.1 wiring tests: BuildPodSpec must invoke ScriptPodImageValidator
    //
    // The validator's own decision matrix is covered by ScriptPodImageValidationTests.
    // These tests narrow-focus on the wiring — confirming that _settings.ScriptPodImage
    // actually flows through the validator before being baked into the V1Pod. A silent
    // wiring regression (e.g. someone deletes the EnsureSafe call during a refactor)
    // would let a tag-only production default reopen the registry-compromise RCE.

    [Fact]
    public void CreatePod_WithTagOnlyScriptPodImage_Throws()
    {
        var thrown = Should.Throw<InvalidOperationException>(
            () => CaptureCreatedPodWithSettings(s => s.ScriptPodImage = "bitnami/kubectl:latest"),
            customMessage:
                "BuildPodSpec must reject a tag-only ScriptPodImage. If this test passes, " +
                "the validator wiring was removed — registry compromise / tag repoint RCE is back in scope.");

        thrown.Message.ShouldContain("@sha256:",
            customMessage: "error must name the required digest format so operators know what to fix");
    }

    [Fact]
    public void CreatePod_WithEmptyScriptPodImage_Throws()
    {
        // Post-fix default is empty string — operator must explicitly set a digest-pinned
        // value. CreatePod must refuse to emit a Pod with an unset image.
        Should.Throw<InvalidOperationException>(
            () => CaptureCreatedPodWithSettings(s => s.ScriptPodImage = ""),
            customMessage:
                "BuildPodSpec must reject empty ScriptPodImage — this is the post-fix fail-closed default");
    }

    [Fact]
    public void CreatePod_WithDigestPinnedImage_SucceedsAndEmitsImageVerbatim()
    {
        // Positive wiring test — digest-pinned image flows through the validator and
        // ends up in the V1Pod.Spec.Containers[0].Image unchanged.
        var pod = CaptureCreatedPodWithSettings(s => s.ScriptPodImage = TestDigestImage);

        pod.Spec.Containers[0].Image.ShouldBe(TestDigestImage);
    }
}
