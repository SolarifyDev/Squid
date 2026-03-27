using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

/// <summary>
/// Tests for deployment YAML fields newly added to DeploymentResourceGenerator:
/// revisionHistoryLimit, progressDeadlineSeconds, maxUnavailable/maxSurge,
/// serviceAccountName, restartPolicy, dnsPolicy, hostNetwork,
/// terminationGracePeriodSeconds, priorityClassName, readinessGates,
/// dnsConfig (nameservers/searches/options), pod-level securityContext, hostAliases.
/// </summary>
public class DeploymentResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    // === Deployment spec fields ===

    [Fact]
    public async Task Generate_RevisionHistoryLimit_IsIncludedInSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.RevisionHistoryLimit", "5");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("revisionHistoryLimit: 5");
    }

    [Fact]
    public async Task Generate_RevisionHistoryLimit_NonInteger_IsOmitted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.RevisionHistoryLimit", "abc");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("revisionHistoryLimit");
    }

    [Fact]
    public async Task Generate_ProgressDeadlineSeconds_IsIncludedInSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ProgressDeadlineSeconds", "600");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("progressDeadlineSeconds: 600");
    }

    // === Rolling update strategy ===

    [Fact]
    public async Task Generate_RollingUpdateWithMaxValues_IncludesRollingUpdateBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");
        Add(action, "Squid.Action.KubernetesContainers.MaxUnavailable", "25%");
        Add(action, "Squid.Action.KubernetesContainers.MaxSurge", "1");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("rollingUpdate:");
        yaml.ShouldContain("maxUnavailable: \"25%\"");
        yaml.ShouldContain("maxSurge: 1");
    }

    [Fact]
    public async Task Generate_RecreateStrategy_OmitsRollingUpdateBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "Recreate");
        Add(action, "Squid.Action.KubernetesContainers.MaxUnavailable", "25%");
        Add(action, "Squid.Action.KubernetesContainers.MaxSurge", "1");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("type: Recreate");
        yaml.ShouldNotContain("rollingUpdate:");
    }

    [Fact]
    public async Task Generate_RollingUpdateWithoutMaxValues_OmitsRollingUpdateBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("type: RollingUpdate");
        yaml.ShouldNotContain("rollingUpdate:");
    }

    // === Pod spec: serviceAccountName ===

    [Fact]
    public async Task Generate_ServiceAccountName_IsIncludedInPodSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ServiceAccountName", "my-service-account");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("\"serviceAccountName\": \"my-service-account\"");
    }

    [Fact]
    public async Task Generate_ServiceAccountNameEmpty_IsOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("serviceAccountName");
    }

    // === Pod spec: restartPolicy ===

    [Fact]
    public async Task Generate_RestartPolicyOnFailure_IsIncludedInPodSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodRestartPolicy", "OnFailure");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("restartPolicy: \"OnFailure\"");
    }

    [Fact]
    public async Task Generate_RestartPolicyAlways_IsOmittedAsDefault()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodRestartPolicy", "Always");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("restartPolicy");
    }

    // === Pod spec: dnsPolicy ===

    [Fact]
    public async Task Generate_DnsPolicyNone_IsIncludedInPodSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsPolicy", "None");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("dnsPolicy: \"None\"");
    }

    [Fact]
    public async Task Generate_DnsPolicyClusterFirst_IsOmittedAsDefault()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsPolicy", "ClusterFirst");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("dnsPolicy");
    }

    // === Pod spec: hostNetwork ===

    [Fact]
    public async Task Generate_HostNetworkTrue_IncludesHostNetworkTrue()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodHostNetworking", "True");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("hostNetwork: true");
    }

    [Fact]
    public async Task Generate_HostNetworkFalse_OmitsHostNetwork()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodHostNetworking", "False");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("hostNetwork");
    }

    // === Pod spec: terminationGracePeriodSeconds ===

    [Fact]
    public async Task Generate_TerminationGracePeriodSeconds_IsIncludedInPodSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodTerminationGracePeriodSeconds", "30");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("terminationGracePeriodSeconds: 30");
    }

    // === Pod spec: priorityClassName ===

    [Fact]
    public async Task Generate_PriorityClassName_IsIncludedInPodSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodPriorityClassName", "high-priority");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("\"priorityClassName\": \"high-priority\"");
    }

    // === Pod spec: readinessGates ===

    [Fact]
    public async Task Generate_ReadinessGatesSingle_IsIncludedAsSingleCondition()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodReadinessGates", "feature-gate-1");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("readinessGates:");
        yaml.ShouldContain("conditionType: \"feature-gate-1\"");
    }

    [Fact]
    public async Task Generate_ReadinessGatesCommaSeparated_GeneratesMultipleConditions()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodReadinessGates", "gate-a, gate-b");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("conditionType: \"gate-a\"");
        yaml.ShouldContain("conditionType: \"gate-b\"");
    }

    [Fact]
    public async Task Generate_ReadinessGatesNewlineSeparated_GeneratesMultipleConditions()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodReadinessGates", "gate-a\ngate-b\ngate-c");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("conditionType: \"gate-a\"");
        yaml.ShouldContain("conditionType: \"gate-b\"");
        yaml.ShouldContain("conditionType: \"gate-c\"");
    }

    // === Deployment strategy mapping ===

    [Fact]
    public async Task Generate_BlueGreenStrategy_OutputsRollingUpdateType()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("type: RollingUpdate");
        yaml.ShouldNotContain("type: BlueGreen");
    }

    // === dnsConfig: nameservers and searches ===

    [Fact]
    public async Task Generate_DnsNameservers_GeneratedAsYamlList()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsNameservers", "8.8.8.8, 8.8.4.4");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("dnsConfig:");
        yaml.ShouldContain("nameservers:");
        yaml.ShouldContain("- \"8.8.8.8\"");
        yaml.ShouldContain("- \"8.8.4.4\"");
    }

    [Fact]
    public async Task Generate_DnsNameservers_NewlineSeparated_GeneratedAsYamlList()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsNameservers", "8.8.8.8\n8.8.4.4");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("nameservers:");
        yaml.ShouldContain("- \"8.8.8.8\"");
        yaml.ShouldContain("- \"8.8.4.4\"");
    }

    [Fact]
    public async Task Generate_DnsSearches_GeneratedAsYamlList()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsSearches", "my.dns.search.suffix, second.suffix");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("dnsConfig:");
        yaml.ShouldContain("searches:");
        yaml.ShouldContain("- \"my.dns.search.suffix\"");
        yaml.ShouldContain("- \"second.suffix\"");
    }

    [Fact]
    public async Task Generate_DnsConfigAllParts_CombinedUnderSingleDnsConfigBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsNameservers", "1.1.1.1");
        Add(action, "Squid.Action.KubernetesContainers.PodDnsSearches", "example.local");
        Add(action, "Squid.Action.KubernetesContainers.DnsConfigOptions",
            """[{"name":"ndots","value":"5"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        var dnsConfigIndex = yaml.IndexOf("dnsConfig:", StringComparison.Ordinal);
        dnsConfigIndex.ShouldBeGreaterThan(-1);
        yaml.ShouldContain("nameservers:");
        yaml.ShouldContain("searches:");
        yaml.ShouldContain("options:");
        yaml.IndexOf("dnsConfig:", dnsConfigIndex + 1, StringComparison.Ordinal)
            .ShouldBe(-1, "dnsConfig: should appear only once");
    }

    // === Pod-level securityContext ===

    [Fact]
    public async Task Generate_PodSecurityFsGroup_IsIncludedInSecurityContext()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityFsGroup", "1000");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("fsGroup: 1000");
    }

    [Fact]
    public async Task Generate_PodSecurityRunAsUserAndGroup_IsIncludedInSecurityContext()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityRunAsUser", "1001");
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityRunAsGroup", "2001");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("runAsUser: 1001");
        yaml.ShouldContain("runAsGroup: 2001");
    }

    [Fact]
    public async Task Generate_PodSecurityRunAsNonRootTrue_IncludesRunAsNonRootTrue()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityRunAsNonRoot", "True");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("runAsNonRoot: true");
    }

    [Fact]
    public async Task Generate_PodSecurityRunAsNonRootFalse_OmitsRunAsNonRoot()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityRunAsNonRoot", "False");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("runAsNonRoot");
    }

    [Fact]
    public async Task Generate_PodSecuritySupplementalGroups_IsIncludedAsList()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySupplementalGroups", "1000, 2000");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("supplementalGroups:");
        yaml.ShouldContain("- 1000");
        yaml.ShouldContain("- 2000");
    }

    [Fact]
    public async Task Generate_PodSecuritySeLinuxOptions_AllFieldsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySeLinuxLevel", "s0:c123,c456");
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySeLinuxRole", "system_r");
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySeLinuxType", "svirt_lxc_net_t");
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySeLinuxUser", "system_u");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("seLinuxOptions:");
        yaml.ShouldContain("\"level\": \"s0:c123,c456\"");
        yaml.ShouldContain("\"role\": \"system_r\"");
        yaml.ShouldContain("\"type\": \"svirt_lxc_net_t\"");
        yaml.ShouldContain("\"user\": \"system_u\"");
    }

    [Fact]
    public async Task Generate_PodSecuritySysctls_IsIncludedInSecurityContext()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySysctls",
            """[{"name":"net.core.somaxconn","value":"1024"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("sysctls:");
        yaml.ShouldContain("net.core.somaxconn");
    }

    [Fact]
    public async Task Generate_PodSecurityAllFields_ProducedUnderSingleSecurityContextBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityFsGroup", "1000");
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityRunAsUser", "1001");
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityRunAsNonRoot", "True");
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySupplementalGroups", "500");
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySeLinuxLevel", "s0");
        Add(action, "Squid.Action.KubernetesContainers.PodSecuritySysctls",
            """[{"name":"net.core.somaxconn","value":"1024"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("fsGroup: 1000");
        yaml.ShouldContain("runAsUser: 1001");
        yaml.ShouldContain("runAsNonRoot: true");
        yaml.ShouldContain("supplementalGroups:");
        yaml.ShouldContain("seLinuxOptions:");
        yaml.ShouldContain("sysctls:");

        var firstIndex = yaml.IndexOf("securityContext:", StringComparison.Ordinal);
        firstIndex.ShouldBeGreaterThan(-1);

        // Verify there is only one pod-level securityContext (not counting container-level ones
        // which would appear indented further). The pod-level one should be at 6 spaces indent.
        var podLevelSecCtx = "      securityContext:";
        yaml.IndexOf(podLevelSecCtx, StringComparison.Ordinal)
            .ShouldBeGreaterThan(-1, "pod-level securityContext should exist at 6-space indent");
        yaml.IndexOf(podLevelSecCtx,
                yaml.IndexOf(podLevelSecCtx, StringComparison.Ordinal) + 1,
                StringComparison.Ordinal)
            .ShouldBe(-1, "pod-level securityContext should appear only once");
    }

    // === hostAliases ===

    [Fact]
    public async Task Generate_HostAliases_IsIncludedInPodSpec()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.HostAliases",
            """[{"ip":"127.0.0.1","hostnames":["foo.local","bar.local"]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("hostAliases:");
        yaml.ShouldContain("127.0.0.1");
        yaml.ShouldContain("foo.local");
    }

    [Fact]
    public async Task Generate_HostAliasesEmptyArray_IsOmitted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.HostAliases", "[]");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("hostAliases");
    }

    // === Volume types ===

    [Fact]
    public async Task Generate_VolumeTypeConfigMap_IncludesConfigMapBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"cm-vol","Type":"ConfigMap","ReferenceName":"my-cm"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"cm-vol\"");
        yaml.ShouldContain("configMap:");
        yaml.ShouldContain("name: \"my-cm\"");
    }

    [Fact]
    public async Task Generate_VolumeTypeSecret_IncludesSecretBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"secret-vol","Type":"Secret","ReferenceName":"my-secret"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"secret-vol\"");
        yaml.ShouldContain("secret:");
        yaml.ShouldContain("secretName: \"my-secret\"");
    }

    [Fact]
    public async Task Generate_VolumeTypeEmptyDir_IncludesEmptyDirBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"empty-vol","Type":"EmptyDir","ReferenceName":""}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"empty-vol\"");
        yaml.ShouldContain("emptyDir: {}");
    }

    [Fact]
    public async Task Generate_VolumeTypePVC_IncludesPersistentVolumeClaimBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"pvc-vol","Type":"PVC","ReferenceName":"my-pvc"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"pvc-vol\"");
        yaml.ShouldContain("persistentVolumeClaim:");
        yaml.ShouldContain("claimName: \"my-pvc\"");
    }

    [Fact]
    public async Task Generate_VolumeTypeHostPath_IncludesHostPathBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"hp-vol","Type":"HostPath","ReferenceName":"/var/log"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"hp-vol\"");
        yaml.ShouldContain("hostPath:");
        yaml.ShouldContain("path: \"/var/log\"");
    }

    // === LinkedResource volume resolution ===

    [Fact]
    public async Task Generate_VolumeLinkedConfigMap_ResolvesFromStepConfigMapName()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "my-config");
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"cfg-vol","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"cfg-vol\"");
        yaml.ShouldContain("configMap:");
        yaml.ShouldContain("name: \"my-config\"");
    }

    [Fact]
    public async Task Generate_VolumeLinkedConfigMap_WithSuffix_ResolvesAndAppendsSuffix()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "my-config");
        Add(action, "Squid.Internal.DeploymentIdSuffix", "deployments-42");
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"cfg-vol","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("name: \"my-config-deployments-42\"");
    }

    [Fact]
    public async Task Generate_VolumeCustomResource_UsesExplicitReferenceName()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "step-config");
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"ext-vol","Type":"ConfigMap","ReferenceName":"external-cm","ResourceNameMode":"CustomResource"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("name: \"external-cm\"");
        yaml.ShouldNotContain("step-config");
    }

    [Fact]
    public async Task Generate_VolumeLinkedConfigMap_NoConfigMapName_SkipsVolume()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"cfg-vol","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"cfg-vol\"");
        yaml.ShouldNotContain("configMap:");
    }

    // === ConfigMap volume Items ===

    [Fact]
    public async Task Generate_VolumeConfigMapWithItems_GeneratesItemsSection()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"cfg-vol","Type":"ConfigMap","ReferenceName":"my-cm","Items":[{"key":".env","path":".env"},{"key":"config.json","path":"app/config.json"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("configMap:");
        yaml.ShouldContain("name: \"my-cm\"");
        yaml.ShouldContain("items:");
        yaml.ShouldContain("- key: \".env\"");
        yaml.ShouldContain("path: \".env\"");
        yaml.ShouldContain("- key: \"config.json\"");
        yaml.ShouldContain("path: \"app/config.json\"");
    }

    [Fact]
    public async Task Generate_VolumeConfigMapWithoutItems_OmitsItemsSection()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"cfg-vol","Type":"ConfigMap","ReferenceName":"my-cm"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("configMap:");
        yaml.ShouldContain("name: \"my-cm\"");
        yaml.ShouldNotContain("items:");
    }

    [Fact]
    public async Task Generate_VolumeSecretWithItems_GeneratesItemsSection()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"secret-vol","Type":"Secret","ReferenceName":"my-secret","Items":[{"key":"tls.crt","path":"cert.pem"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("secret:");
        yaml.ShouldContain("secretName: \"my-secret\"");
        yaml.ShouldContain("items:");
        yaml.ShouldContain("- key: \"tls.crt\"");
        yaml.ShouldContain("path: \"cert.pem\"");
    }

    [Fact]
    public async Task Generate_VolumeLinkedConfigMapWithItems_ResolvesNameAndIncludesItems()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ConfigMapName", "configurations-squidweb");
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"squidweb-env","Type":"ConfigMap","ReferenceName":"","ResourceNameMode":"LinkedResource","Items":[{"key":".env","path":".env"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"squidweb-env\"");
        yaml.ShouldContain("configMap:");
        yaml.ShouldContain("name: \"configurations-squidweb\"");
        yaml.ShouldContain("items:");
        yaml.ShouldContain("- key: \".env\"");
        yaml.ShouldContain("path: \".env\"");
    }

    // === JSON passthrough formatting (tolerations, affinity, hostAliases) ===

    [Fact]
    public async Task Generate_Tolerations_DashInlineWithFirstProperty()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Equal","value":"1","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- key: test");
        yaml.ShouldNotContain("- key: \"test\"");
    }

    [Fact]
    public async Task Generate_Tolerations_SimpleStringValues_NotQuoted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"node-role","operator":"Exists","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("key: node-role");
        yaml.ShouldContain("operator: Exists");
        yaml.ShouldContain("effect: NoSchedule");
        yaml.ShouldNotContain("\"node-role\"");
        yaml.ShouldNotContain("\"Exists\"");
        yaml.ShouldNotContain("\"NoSchedule\"");
    }

    [Fact]
    public async Task Generate_Tolerations_NumericStringValue_IsSingleQuoted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Equal","value":"1","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("value: '1'");
        yaml.ShouldNotContain("value: \"1\"");
    }

    [Fact]
    public async Task Generate_Tolerations_EmptyStringEffect_IsSingleQuotedEmpty()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Exists","effect":""}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("effect: ''");
        yaml.ShouldNotContain("effect: \"\"");
    }

    [Fact]
    public async Task Generate_Tolerations_BooleanStringValue_IsSingleQuoted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Equal","value":"true","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("value: 'true'");
        yaml.ShouldNotContain("value: true\n");
        yaml.ShouldNotContain("value: \"true\"");
    }

    [Fact]
    public async Task Generate_Tolerations_FullSquidExample_MatchesConventionalFormat()
    {
        // Verifies exact output matches the reference Squid YAML provided during review
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"equal","value":"1","effect":""}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- key: test");
        yaml.ShouldContain("operator: equal");
        yaml.ShouldContain("value: '1'");
        yaml.ShouldContain("effect: ''");
    }

    [Fact]
    public async Task Generate_Tolerations_ExistsOperator_OmitsValueField()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Exists","value":"1","effect":""}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- key: test");
        yaml.ShouldContain("operator: Exists");
        yaml.ShouldContain("effect: ''");
        yaml.ShouldNotContain("value:");
    }

    [Fact]
    public async Task Generate_Tolerations_ExistsOperatorCaseInsensitive_OmitsValueField()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"node-role","operator":"exists","value":"something","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("operator: exists");
        yaml.ShouldNotContain("value:");
    }

    [Fact]
    public async Task Generate_Tolerations_EqualOperator_KeepsValueField()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Equal","value":"1","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("value: '1'");
    }

    [Fact]
    public async Task Generate_Tolerations_MixedOperators_OnlyExistsOmitsValue()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"k1","operator":"Equal","value":"v1","effect":"NoSchedule"},{"key":"k2","operator":"Exists","value":"should-be-removed","effect":"NoExecute"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("value: v1");
        var tolerationsSection = yaml[yaml.IndexOf("tolerations:", StringComparison.Ordinal)..];
        var k2Section = tolerationsSection[tolerationsSection.IndexOf("k2", StringComparison.Ordinal)..];
        k2Section.ShouldNotContain("should-be-removed");
    }

    [Fact]
    public async Task Generate_Tolerations_ExistsWithEmptyValue_StillOmitsValueField()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key":"test","operator":"Exists","value":"","effect":"NoSchedule"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("operator: Exists");
        // Empty value with Exists is harmless but cleaner to omit
    }

    [Fact]
    public async Task Generate_HostAliases_SpaceSeparatedString_GeneratesList()
    {
        // Frontend sends hostnames as a space-separated string
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.HostAliases",
            """[{"ip":"127.0.0.1","hostnames":"foo.local bar.local"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- ip: \"127.0.0.1\"");
        yaml.ShouldContain("hostnames:");
        yaml.ShouldContain("- \"foo.local\"");
        yaml.ShouldContain("- \"bar.local\"");
        yaml.ShouldNotContain("hostnames: foo.local bar.local");
    }

    [Fact]
    public async Task Generate_HostAliases_ArrayHostnames_GeneratesList()
    {
        // Handles array hostnames format for robustness
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.HostAliases",
            """[{"ip":"127.0.0.1","hostnames":["foo.local","bar.local"]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- ip: \"127.0.0.1\"");
        yaml.ShouldContain("hostnames:");
        yaml.ShouldContain("- \"foo.local\"");
        yaml.ShouldContain("- \"bar.local\"");
    }

    [Fact]
    public async Task Generate_NodeAffinity_DashInlineWithFirstProperty()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.NodeAffinity",
            """{"requiredDuringSchedulingIgnoredDuringExecution":{"nodeSelectorTerms":[{"matchExpressions":[{"key":"kubernetes.io/os","operator":"In","values":["linux"]}]}]}}""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- matchExpressions:");
        yaml.ShouldContain("- key: kubernetes.io/os");
        yaml.ShouldContain("operator: In");
        yaml.ShouldContain("- linux");
        yaml.ShouldNotContain("\"kubernetes.io/os\"");
        yaml.ShouldNotContain("\"In\"");
    }

    // === Init containers ===

    [Fact]
    public async Task Generate_InitContainer_GoesToInitContainersSection()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"init-app","Image":"busybox:latest","IsInitContainer":"True"},{"Name":"app","Image":"nginx:latest"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("initContainers:");
        var initIndex = yaml.IndexOf("initContainers:", StringComparison.Ordinal);
        var containersIndex = yaml.IndexOf("containers:", StringComparison.Ordinal);
        yaml.IndexOf("init-app", initIndex, containersIndex - initIndex, StringComparison.Ordinal)
            .ShouldBeGreaterThan(-1, "init-app should appear under initContainers:");
    }

    [Fact]
    public async Task Generate_RegularContainer_NotInInitContainersSection()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("initContainers:");
        yaml.ShouldContain("containers:");
    }

    [Fact]
    public async Task Generate_MixedContainers_SplitCorrectly()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"init-app","Image":"busybox:latest","IsInitContainer":"True"},{"Name":"app","Image":"nginx:latest"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("initContainers:");
        yaml.ShouldContain("containers:");

        var initIndex = yaml.IndexOf("initContainers:", StringComparison.Ordinal);
        var containersIndex = yaml.IndexOf("containers:", StringComparison.Ordinal);

        yaml.IndexOf("init-app", initIndex, containersIndex - initIndex, StringComparison.Ordinal)
            .ShouldBeGreaterThan(-1, "init-app should appear under initContainers:");
        yaml.IndexOf("- name: \"app\"", containersIndex, StringComparison.Ordinal)
            .ShouldBeGreaterThan(-1, "app should appear under containers:");
    }

    [Fact]
    public async Task Generate_InitContainersAppearsBeforeContainers_InYaml()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"init-app","Image":"busybox:latest","IsInitContainer":"True"},{"Name":"app","Image":"nginx:latest"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.IndexOf("initContainers:", StringComparison.Ordinal)
            .ShouldBeLessThan(yaml.IndexOf("containers:", StringComparison.Ordinal));
    }

    // === Probes ===

    [Fact]
    public async Task Generate_HttpGetProbe_NoTypeField_InYaml()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","LivenessProbe":{"type":"httpGet","httpGet":{"path":"/health","port":"8080","scheme":"HTTP"},"initialDelaySeconds":"10","periodSeconds":"5","failureThreshold":"3","successThreshold":"","timeoutSeconds":""}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("livenessProbe:");
        yaml.ShouldContain("httpGet:");
        yaml.ShouldContain("\"path\": \"/health\"");
        yaml.ShouldContain("port: 8080");
        yaml.ShouldContain("\"scheme\": \"HTTP\"");
        yaml.ShouldContain("initialDelaySeconds: 10");
        yaml.ShouldContain("periodSeconds: 5");
        yaml.ShouldContain("failureThreshold: 3");
        yaml.ShouldNotContain("type: httpGet");
    }

    [Fact]
    public async Task Generate_ExecProbe_StringCommand_GeneratesExecBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ReadinessProbe":{"type":"exec","exec":{"command":"cat /tmp/healthy"},"initialDelaySeconds":"5","periodSeconds":"","failureThreshold":"","successThreshold":"","timeoutSeconds":""}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("readinessProbe:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("command:");
        yaml.ShouldContain("- \"cat /tmp/healthy\"");
        yaml.ShouldNotContain("type: exec");
    }

    [Fact]
    public async Task Generate_ExecProbe_MultilineStringCommand_SplitsIntoList()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","StartupProbe":{"type":"exec","exec":{"command":"/bin/sh\n-c\ncat /tmp/healthy"},"initialDelaySeconds":"","periodSeconds":"","failureThreshold":"","successThreshold":"","timeoutSeconds":""}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("startupProbe:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("- \"/bin/sh\"");
        yaml.ShouldContain("- \"-c\"");
        yaml.ShouldContain("- \"cat /tmp/healthy\"");
    }

    [Fact]
    public async Task Generate_TcpSocketProbe_NoTypeField_InYaml()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","LivenessProbe":{"type":"tcpSocket","tcpSocket":{"port":"3306"},"initialDelaySeconds":"","periodSeconds":"","failureThreshold":"","successThreshold":"","timeoutSeconds":""}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("livenessProbe:");
        yaml.ShouldContain("tcpSocket:");
        yaml.ShouldContain("port: 3306");
        yaml.ShouldNotContain("type: tcpSocket");
    }

    // === Lifecycle ===

    [Fact]
    public async Task Generate_LifecyclePreStop_ExecStringCommand_GeneratesPreStopBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Lifecycle":{"preStop":{"type":"exec","command":"sleep 5"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("preStop:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("command:");
        yaml.ShouldContain("- \"sleep 5\"");
    }

    [Fact]
    public async Task Generate_LifecyclePostStart_ExecStringCommand_GeneratesPostStartBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Lifecycle":{"postStart":{"type":"exec","command":"echo started"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("postStart:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("- \"echo started\"");
    }

    [Theory]
    [InlineData("preStop")]
    [InlineData("postStart")]
    public async Task Generate_LifecycleHandler_EmptyType_OmitsHandler(string hookName)
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            $"[{{\"Name\":\"app\",\"Image\":\"nginx:latest\",\"Lifecycle\":{{\"{hookName}\":{{\"type\":\"\",\"command\":\"\"}}}}}}]");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("lifecycle:");
    }

    // === Container: envFrom ===

    [Fact]
    public async Task Generate_ConfigMapEnvFromSource_GeneratesEnvFromBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvFromSource":[{"key":"app-config","value":"","option":""}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("envFrom:");
        yaml.ShouldContain("- configMapRef:");
        yaml.ShouldContain("name: \"app-config\"");
    }

    [Fact]
    public async Task Generate_MultipleConfigMapEnvFromSources_GeneratesMultipleConfigMapRefs()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvFromSource":[{"key":"config-a","value":"","option":""},{"key":"config-b","value":"","option":""}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("envFrom:");
        yaml.ShouldContain("name: \"config-a\"");
        yaml.ShouldContain("name: \"config-b\"");

        var firstRef = yaml.IndexOf("configMapRef:", StringComparison.Ordinal);
        var secondRef = yaml.IndexOf("configMapRef:", firstRef + 1, StringComparison.Ordinal);
        secondRef.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Generate_NoConfigMapEnvFromSource_EnvFromBlockOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("envFrom:");
    }

    // === Container: env (literal) ===

    [Fact]
    public async Task Generate_EnvironmentVariables_GeneratesEnvBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","EnvironmentVariables":[{"key":"DB_HOST","value":"localhost"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("env:");
        yaml.ShouldContain("- name: \"DB_HOST\"");
        yaml.ShouldContain("\"value\": localhost");
    }

    [Fact]
    public async Task Generate_EnvironmentVariables_Multiple_AllGenerated()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","EnvironmentVariables":[{"key":"DB_HOST","value":"localhost"},{"key":"DB_PORT","value":"5432"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"DB_HOST\"");
        yaml.ShouldContain("\"value\": localhost");
        yaml.ShouldContain("- name: \"DB_PORT\"");
    }

    [Fact]
    public async Task Generate_EnvironmentVariables_NumericValue_IsQuoted()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","EnvironmentVariables":[{"key":"PORT","value":"8080"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("\"value\": '8080'");
    }

    [Fact]
    public async Task Generate_NoEnvironmentVariables_EnvBlockOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("env:");
    }

    // === Container: env (configMapKeyRef) ===

    [Fact]
    public async Task Generate_ConfigMapEnvVariables_GeneratesConfigMapKeyRef()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvironmentVariables":[{"key":"CONFIG_VAR","value":"my-config","option":"my-key"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("env:");
        yaml.ShouldContain("- name: \"CONFIG_VAR\"");
        yaml.ShouldContain("configMapKeyRef:");
        yaml.ShouldContain("name: \"my-config\"");
        yaml.ShouldContain("key: \"my-key\"");
    }

    [Fact]
    public async Task Generate_ConfigMapEnvVariables_Multiple_AllGenerated()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvironmentVariables":[{"key":"VAR_A","value":"cm-a","option":"key-a"},{"key":"VAR_B","value":"cm-b","option":"key-b"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: \"VAR_A\"");
        yaml.ShouldContain("- name: \"VAR_B\"");
    }

    // === Container: env (secretKeyRef) ===

    [Fact]
    public async Task Generate_SecretEnvVariables_GeneratesSecretKeyRef()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecretEnvironmentVariables":[{"key":"SECRET_VAR","value":"my-secret","option":"secret-key"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("env:");
        yaml.ShouldContain("- name: \"SECRET_VAR\"");
        yaml.ShouldContain("secretKeyRef:");
        yaml.ShouldContain("name: \"my-secret\"");
        yaml.ShouldContain("key: \"secret-key\"");
    }

    // === Container: env (fieldRef) ===

    [Fact]
    public async Task Generate_FieldRefEnvVariables_GeneratesFieldRef()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","FieldRefEnvironmentVariables":[{"key":"POD_NAME","value":"metadata.name"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("env:");
        yaml.ShouldContain("- name: \"POD_NAME\"");
        yaml.ShouldContain("fieldRef:");
        yaml.ShouldContain("fieldPath: \"metadata.name\"");
    }

    [Fact]
    public async Task Generate_FieldRefEnvVariables_PodIP()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","FieldRefEnvironmentVariables":[{"key":"POD_IP","value":"status.podIP"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("fieldPath: \"status.podIP\"");
    }

    // === Container: envFrom (secretRef) ===

    [Fact]
    public async Task Generate_SecretEnvFromSource_GeneratesSecretRef()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecretEnvFromSource":[{"key":"my-secret"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("envFrom:");
        yaml.ShouldContain("- secretRef:");
        yaml.ShouldContain("name: \"my-secret\"");
    }

    [Fact]
    public async Task Generate_BothEnvFromSources_CombinedUnderSingleBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvFromSource":[{"key":"app-config"}],"SecretEnvFromSource":[{"key":"app-secret"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("envFrom:");
        yaml.ShouldContain("configMapRef:");
        yaml.ShouldContain("name: \"app-config\"");
        yaml.ShouldContain("secretRef:");
        yaml.ShouldContain("name: \"app-secret\"");

        var envFromIndex = yaml.IndexOf("envFrom:", StringComparison.Ordinal);
        yaml.IndexOf("envFrom:", envFromIndex + 1, StringComparison.Ordinal)
            .ShouldBe(-1, "envFrom: should appear only once");
    }

    // === Container: envFrom prefix and optional ===

    [Fact]
    public async Task Generate_ConfigMapEnvFromSource_WithPrefixAndOptional_GeneratesBothFields()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvFromSource":[{"key":"app-config","value":"CFG_","option":"True"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("envFrom:");
        yaml.ShouldContain("configMapRef:");
        yaml.ShouldContain("name: \"app-config\"");
        yaml.ShouldContain("optional: true");
        yaml.ShouldContain("prefix: \"CFG_\"");
    }

    [Fact]
    public async Task Generate_SecretEnvFromSource_WithPrefixAndOptional_GeneratesBothFields()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecretEnvFromSource":[{"key":"my-secret","value":"SEC_","option":"False"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("secretRef:");
        yaml.ShouldContain("name: \"my-secret\"");
        yaml.ShouldContain("optional: false");
        yaml.ShouldContain("prefix: \"SEC_\"");
    }

    [Fact]
    public async Task Generate_EnvFromSource_NoPrefixNoOptional_OmitsBothFields()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvFromSource":[{"key":"app-config"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("configMapRef:");
        yaml.ShouldContain("name: \"app-config\"");
        yaml.ShouldNotContain("optional:");
        yaml.ShouldNotContain("prefix:");
    }

    // === Container: env optional (configMapKeyRef / secretKeyRef) ===

    [Fact]
    public async Task Generate_ConfigMapEnvVariable_WithOptionalTrue_IncludesOptional()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvironmentVariables":[{"key":"VAR","value":"cm","option":"k","optional":"True"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("configMapKeyRef:");
        yaml.ShouldContain("optional: true");
    }

    [Fact]
    public async Task Generate_SecretEnvVariable_WithOptionalFalse_IncludesOptional()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecretEnvironmentVariables":[{"key":"VAR","value":"sec","option":"k","optional":"False"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("secretKeyRef:");
        yaml.ShouldContain("optional: false");
    }

    [Fact]
    public async Task Generate_ConfigMapEnvVariable_NoOptional_OmitsOptional()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ConfigMapEnvironmentVariables":[{"key":"VAR","value":"cm","option":"k"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("configMapKeyRef:");
        yaml.ShouldNotContain("optional:");
    }

    // === Container: imagePullPolicy ===

    [Fact]
    public async Task Generate_ImagePullPolicy_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","ImagePullPolicy":"Always"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("imagePullPolicy: \"Always\"");
    }

    [Fact]
    public async Task Generate_ImagePullPolicy_Empty_Omitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("imagePullPolicy");
    }

    // === Container: command / args ===

    [Fact]
    public async Task Generate_Command_GeneratesCommandList()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"node:latest","Command":["node","server.js"]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("command:");
        yaml.ShouldContain("- \"node\"");
        yaml.ShouldContain("- \"server.js\"");
    }

    [Fact]
    public async Task Generate_Args_GeneratesArgsList()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"node:latest","Args":["--port","3000"]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("args:");
        yaml.ShouldContain("- \"--port\"");
        yaml.ShouldContain("- \"3000\"");
    }

    [Fact]
    public async Task Generate_EmptyCommand_Omitted()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Command":[]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("command:");
    }

    // === Container: terminationMessage ===

    [Fact]
    public async Task Generate_TerminationMessagePath_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","TerminationMessagePath":"/dev/termination-log"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("terminationMessagePath: \"/dev/termination-log\"");
    }

    [Fact]
    public async Task Generate_TerminationMessagePolicy_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","TerminationMessagePolicy":"FallbackToLogsOnError"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("terminationMessagePolicy: \"FallbackToLogsOnError\"");
    }

    [Fact]
    public async Task Generate_TerminationMessage_Empty_Omitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("terminationMessagePath");
        yaml.ShouldNotContain("terminationMessagePolicy");
    }

    // === Combined env scenario ===

    [Fact]
    public async Task Generate_AllEnvTypes_CombinedInSingleEnvBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","EnvironmentVariables":[{"key":"PLAIN","value":"val"}],"ConfigMapEnvironmentVariables":[{"key":"CM_VAR","value":"cm","option":"k"}],"SecretEnvironmentVariables":[{"key":"SEC_VAR","value":"sec","option":"sk"}],"FieldRefEnvironmentVariables":[{"key":"POD","value":"metadata.name"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("env:");
        yaml.ShouldContain("- name: \"PLAIN\"");
        yaml.ShouldContain("configMapKeyRef:");
        yaml.ShouldContain("secretKeyRef:");
        yaml.ShouldContain("fieldRef:");

        var envIndex = yaml.IndexOf("        env:", StringComparison.Ordinal);
        yaml.IndexOf("        env:", envIndex + 1, StringComparison.Ordinal)
            .ShouldBe(-1, "env: should appear only once");
    }

    // === Command with newline-separated string ===

    [Fact]
    public async Task Generate_Command_NewlineSeparatedString_SplitsIntoList()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"node:latest","Command":"/bin/sh\n-c\necho hello"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("command:");
        yaml.ShouldContain("- \"/bin/sh\"");
        yaml.ShouldContain("- \"-c\"");
        yaml.ShouldContain("- \"echo hello\"");
    }

    // === Deploy-id annotation (rolling update) ===

    [Fact]
    public async Task Generate_WithDeploymentIdSuffix_InjectsDeployIdPodAnnotation()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Internal.DeploymentIdSuffix", "deployments-42");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("\"squid.io/deploy-id\": \"deployments-42\"");
    }

    [Fact]
    public async Task Generate_WithoutDeploymentIdSuffix_NoDeployIdAnnotation()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("deploy-id");
    }

    [Fact]
    public async Task Generate_WithDeploymentIdSuffix_MergesWithExistingPodAnnotations()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodAnnotations", """[{"key":"prometheus.io/scrape","value":"true"}]""");
        Add(action, "Squid.Internal.DeploymentIdSuffix", "deployments-99");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("\"prometheus.io/scrape\": \"true\"");
        yaml.ShouldContain("\"squid.io/deploy-id\": \"deployments-99\"");
    }

    // === Helpers ===

    private async Task<string> GetDeploymentYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        result.ShouldContainKey("deployment.yaml");
        return Encoding.UTF8.GetString(result["deployment.yaml"]);
    }

    // === Replicas Zero Fix (P1-1) ===

    [Fact]
    public async Task Generate_ReplicasZero_GeneratesReplicasZero()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Replicas", "0");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("replicas: 0");
    }

    // === Resource Type — Default still Deployment ===

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Deployment")]
    public async Task Generate_ResourceTypeDefaultOrDeployment_GeneratesDeploymentKind(string resourceType)
    {
        var (step, action) = CreateMinimal();

        if (resourceType != null)
            Add(action, "Squid.Action.KubernetesContainers.DeploymentResourceType", resourceType);

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("deployment.yaml");
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("kind: Deployment");
    }

    // === Container: securityContext ===

    [Fact]
    public async Task Generate_ContainerSecurityContext_AllowPrivilegeEscalation_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"allowPrivilegeEscalation":"True"}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("allowPrivilegeEscalation: true");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_Privileged_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"privileged":"True"}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("privileged: true");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_ReadOnlyRootFilesystem_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"readOnlyRootFilesystem":"True"}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("readOnlyRootFilesystem: true");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_RunAsUserAndGroup_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"runAsUser":"1000","runAsGroup":"2000"}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("runAsUser: 1000");
        yaml.ShouldContain("runAsGroup: 2000");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_RunAsNonRoot_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"runAsNonRoot":"True"}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("runAsNonRoot: true");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_CapabilitiesAddAndDrop_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"capabilities":{"add":["NET_ADMIN"],"drop":["ALL"]}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("capabilities:");
        yaml.ShouldContain("add:");
        yaml.ShouldContain("- \"NET_ADMIN\"");
        yaml.ShouldContain("drop:");
        yaml.ShouldContain("- \"ALL\"");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_SeLinuxOptions_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","SecurityContext":{"seLinuxOptions":{"level":"s0","role":"system_r","type":"svirt","user":"system_u"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("seLinuxOptions:");
        yaml.ShouldContain("\"level\": \"s0\"");
        yaml.ShouldContain("\"role\": \"system_r\"");
        yaml.ShouldContain("\"type\": \"svirt\"");
        yaml.ShouldContain("\"user\": \"system_u\"");
    }

    [Fact]
    public async Task Generate_ContainerSecurityContext_Empty_IsOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("securityContext");
    }

    // === Container: resources ===

    [Fact]
    public async Task Generate_ContainerResources_RequestsAndLimits_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Resources":{"requests":{"cpu":"100m","memory":"128Mi"},"limits":{"cpu":"500m","memory":"256Mi"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("resources:");
        yaml.ShouldContain("requests:");
        yaml.ShouldContain("\"cpu\": \"100m\"");
        yaml.ShouldContain("\"memory\": \"128Mi\"");
        yaml.ShouldContain("limits:");
        yaml.ShouldContain("\"memory\": \"256Mi\"");
    }

    [Fact]
    public async Task Generate_ContainerResources_OnlyRequests_OmitsLimits()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Resources":{"requests":{"cpu":"100m"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("requests:");
        yaml.ShouldNotContain("limits:");
    }

    [Fact]
    public async Task Generate_ContainerResources_Empty_IsOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("resources:");
    }

    // === Container: volumeMount subPath ===

    [Fact]
    public async Task Generate_VolumeMount_WithSubPath_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","VolumeMounts":[{"key":"vol","value":"/app/config","option":"app.conf"}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("volumeMounts:");
        yaml.ShouldContain("subPath: \"app.conf\"");
    }

    [Fact]
    public async Task Generate_VolumeMount_WithoutSubPath_IsOmitted()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","VolumeMounts":[{"key":"vol","value":"/app/config","option":""}]}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("volumeMounts:");
        yaml.ShouldNotContain("subPath");
    }

    // === Lifecycle: httpGet & tcpSocket ===

    [Fact]
    public async Task Generate_LifecyclePreStop_HttpGet_GeneratesHttpGetBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Lifecycle":{"preStop":{"type":"httpGet","path":"/shutdown","port":"8080","scheme":"HTTP"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("preStop:");
        yaml.ShouldContain("httpGet:");
        yaml.ShouldContain("\"path\": \"/shutdown\"");
        yaml.ShouldContain("\"port\": \"8080\"");
        yaml.ShouldContain("\"scheme\": \"HTTP\"");
    }

    [Fact]
    public async Task Generate_LifecyclePostStart_HttpGet_GeneratesHttpGetBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Lifecycle":{"postStart":{"type":"httpGet","path":"/init","port":"8080"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("postStart:");
        yaml.ShouldContain("httpGet:");
        yaml.ShouldContain("\"path\": \"/init\"");
    }

    [Fact]
    public async Task Generate_LifecyclePreStop_TcpSocket_GeneratesTcpSocketBlock()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Lifecycle":{"preStop":{"type":"tcpSocket","port":"3306"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("preStop:");
        yaml.ShouldContain("tcpSocket:");
        yaml.ShouldContain("\"port\": \"3306\"");
    }

    [Fact]
    public async Task Generate_LifecycleBothHooks_GeneratesBothBlocks()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Lifecycle":{"preStop":{"type":"exec","command":"sleep 5"},"postStart":{"type":"httpGet","path":"/init","port":"8080"}}}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("preStop:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("postStart:");
        yaml.ShouldContain("httpGet:");
    }

    // === Pod affinity & anti-affinity ===

    [Fact]
    public async Task Generate_PodAffinity_IsIncludedUnderAffinity()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodAffinity",
            """{"requiredDuringSchedulingIgnoredDuringExecution":[{"labelSelector":{"matchExpressions":[{"key":"app","operator":"In","values":["web"]}]},"topologyKey":"kubernetes.io/hostname"}]}""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("affinity:");
        yaml.ShouldContain("podAffinity:");
        yaml.ShouldContain("topologyKey: kubernetes.io/hostname");
    }

    [Fact]
    public async Task Generate_PodAntiAffinity_IsIncludedUnderAffinity()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodAntiAffinity",
            """{"requiredDuringSchedulingIgnoredDuringExecution":[{"labelSelector":{"matchExpressions":[{"key":"app","operator":"In","values":["web"]}]},"topologyKey":"kubernetes.io/hostname"}]}""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("affinity:");
        yaml.ShouldContain("podAntiAffinity:");
    }

    [Fact]
    public async Task Generate_AllAffinityTypes_CombinedUnderSingleAffinityBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.NodeAffinity",
            """{"requiredDuringSchedulingIgnoredDuringExecution":{"nodeSelectorTerms":[{"matchExpressions":[{"key":"kubernetes.io/os","operator":"In","values":["linux"]}]}]}}""");
        Add(action, "Squid.Action.KubernetesContainers.PodAffinity",
            """{"requiredDuringSchedulingIgnoredDuringExecution":[{"labelSelector":{"matchExpressions":[{"key":"app","operator":"In","values":["web"]}]},"topologyKey":"kubernetes.io/hostname"}]}""");
        Add(action, "Squid.Action.KubernetesContainers.PodAntiAffinity",
            """{"preferredDuringSchedulingIgnoredDuringExecution":[{"weight":100,"podAffinityTerm":{"labelSelector":{"matchExpressions":[{"key":"app","operator":"In","values":["web"]}]},"topologyKey":"kubernetes.io/hostname"}}]}""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("nodeAffinity:");
        yaml.ShouldContain("podAffinity:");
        yaml.ShouldContain("podAntiAffinity:");

        var affinityCount = 0;
        foreach (var line in yaml.Split('\n'))
        {
            if (line.TrimStart().StartsWith("affinity:", StringComparison.Ordinal))
                affinityCount++;
        }

        affinityCount.ShouldBe(1, "all affinity types should be under a single affinity: block");
    }

    // === ImagePullSecrets ===

    [Fact]
    public async Task Generate_ImagePullSecrets_SingleSecret_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets",
            """[{"name":"my-registry-secret"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("imagePullSecrets:");
        yaml.ShouldContain("- name: \"my-registry-secret\"");
    }

    [Fact]
    public async Task Generate_ImagePullSecrets_MultipleSecrets_AllIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets",
            """[{"name":"secret-a"},{"name":"secret-b"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("imagePullSecrets:");
        yaml.ShouldContain("- name: \"secret-a\"");
        yaml.ShouldContain("- name: \"secret-b\"");
    }

    [Fact]
    public async Task Generate_ImagePullSecrets_EmptyArray_IsOmitted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets", "[]");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("imagePullSecrets");
    }

    // === Pod annotations ===

    [Fact]
    public async Task Generate_PodAnnotations_IsIncludedInTemplate()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodAnnotations",
            """[{"key":"prometheus.io/scrape","value":"true"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("\"prometheus.io/scrape\": \"true\"");
    }

    [Fact]
    public async Task Generate_PodAnnotations_Empty_AnnotationsBlockOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("annotations:");
    }

    [Fact]
    public async Task Generate_PodAnnotations_WithLiteralNewlineInValue_ParsedSuccessfully()
    {
        var (step, action) = CreateMinimal();
        // Simulate variable expansion injecting a literal newline into a JSON string value
        Add(action, "Squid.Action.KubernetesContainers.PodAnnotations",
            "[{\"key\":\"my-annotation\",\"value\":\"line1\nline2\"}]");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("my-annotation");
    }

    [Fact]
    public async Task Generate_EnvironmentVariable_WithLiteralNewlineInValue_ParsedSuccessfully()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        // Simulate variable expansion injecting a literal newline into env var value
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            "[{\"Name\":\"app\",\"Image\":\"nginx:latest\",\"EnvironmentVariables\":[{\"key\":\"CONFIG\",\"value\":\"line1\nline2\"}]}]");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("env:");
        yaml.ShouldContain("- name: \"CONFIG\"");
    }

    // === DNS config: options standalone ===

    [Fact]
    public async Task Generate_DnsConfigOptionsOnly_GeneratesDnsConfigWithOptions()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DnsConfigOptions",
            """[{"name":"ndots","value":"5"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("dnsConfig:");
        yaml.ShouldContain("options:");
        yaml.ShouldContain("name: ndots");
    }

    [Fact]
    public async Task Generate_DnsConfigOptions_EmptyArray_IsOmitted()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DnsConfigOptions", "[]");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldNotContain("dnsConfig");
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.DeploymentName", "test-deploy");
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "test-ns");
        Add(action, "Squid.Action.KubernetesContainers.Containers",
            """[{"Name":"app","Image":"nginx:latest","Ports":[{"key":"http","value":"80","option":"TCP"}]}]""");

        return (step, action);
    }

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = name,
            PropertyValue = value
        });
    }
}
