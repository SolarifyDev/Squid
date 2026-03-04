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
        yaml.ShouldContain("maxUnavailable: 25%");
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

        yaml.ShouldContain("serviceAccountName: my-service-account");
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

        yaml.ShouldContain("restartPolicy: OnFailure");
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

        yaml.ShouldContain("dnsPolicy: None");
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

        yaml.ShouldContain("priorityClassName: high-priority");
    }

    // === Pod spec: readinessGates ===

    [Fact]
    public async Task Generate_ReadinessGatesSingle_IsIncludedAsSingleCondition()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodReadinessGates", "feature-gate-1");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("readinessGates:");
        yaml.ShouldContain("conditionType: feature-gate-1");
    }

    [Fact]
    public async Task Generate_ReadinessGatesCommaSeparated_GeneratesMultipleConditions()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodReadinessGates", "gate-a, gate-b");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("conditionType: gate-a");
        yaml.ShouldContain("conditionType: gate-b");
    }

    [Fact]
    public async Task Generate_ReadinessGatesNewlineSeparated_GeneratesMultipleConditions()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodReadinessGates", "gate-a\ngate-b\ngate-c");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("conditionType: gate-a");
        yaml.ShouldContain("conditionType: gate-b");
        yaml.ShouldContain("conditionType: gate-c");
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
        yaml.ShouldContain("- 8.8.8.8");
        yaml.ShouldContain("- 8.8.4.4");
    }

    [Fact]
    public async Task Generate_DnsNameservers_NewlineSeparated_GeneratedAsYamlList()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsNameservers", "8.8.8.8\n8.8.4.4");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("nameservers:");
        yaml.ShouldContain("- 8.8.8.8");
        yaml.ShouldContain("- 8.8.4.4");
    }

    [Fact]
    public async Task Generate_DnsSearches_GeneratedAsYamlList()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.PodDnsSearches", "my.dns.search.suffix, second.suffix");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("dnsConfig:");
        yaml.ShouldContain("searches:");
        yaml.ShouldContain("- my.dns.search.suffix");
        yaml.ShouldContain("- second.suffix");
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
        yaml.ShouldContain("level: s0:c123,c456");
        yaml.ShouldContain("role: system_r");
        yaml.ShouldContain("type: svirt_lxc_net_t");
        yaml.ShouldContain("user: system_u");
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

        yaml.ShouldContain("- name: cm-vol");
        yaml.ShouldContain("configMap:");
        yaml.ShouldContain("name: my-cm");
    }

    [Fact]
    public async Task Generate_VolumeTypeSecret_IncludesSecretBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"secret-vol","Type":"Secret","ReferenceName":"my-secret"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: secret-vol");
        yaml.ShouldContain("secret:");
        yaml.ShouldContain("secretName: my-secret");
    }

    [Fact]
    public async Task Generate_VolumeTypeEmptyDir_IncludesEmptyDirBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"empty-vol","Type":"EmptyDir","ReferenceName":""}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: empty-vol");
        yaml.ShouldContain("emptyDir: {}");
    }

    [Fact]
    public async Task Generate_VolumeTypePVC_IncludesPersistentVolumeClaimBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"pvc-vol","Type":"PVC","ReferenceName":"my-pvc"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: pvc-vol");
        yaml.ShouldContain("persistentVolumeClaim:");
        yaml.ShouldContain("claimName: my-pvc");
    }

    [Fact]
    public async Task Generate_VolumeTypeHostPath_IncludesHostPathBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.CombinedVolumes",
            """[{"Name":"hp-vol","Type":"HostPath","ReferenceName":"/var/log"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- name: hp-vol");
        yaml.ShouldContain("hostPath:");
        yaml.ShouldContain("path: /var/log");
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
    public async Task Generate_Tolerations_FullOctopusExample_MatchesConventionalFormat()
    {
        // Verifies exact output matches the reference Octopus YAML provided during review
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
    public async Task Generate_HostAliases_SpaceSeparatedString_GeneratesList()
    {
        // Frontend sends hostnames as a space-separated string
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.HostAliases",
            """[{"ip":"127.0.0.1","hostnames":"foo.local bar.local"}]""");

        var yaml = await GetDeploymentYaml(step, action);

        yaml.ShouldContain("- ip: 127.0.0.1");
        yaml.ShouldContain("hostnames:");
        yaml.ShouldContain("- foo.local");
        yaml.ShouldContain("- bar.local");
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

        yaml.ShouldContain("- ip: 127.0.0.1");
        yaml.ShouldContain("hostnames:");
        yaml.ShouldContain("- foo.local");
        yaml.ShouldContain("- bar.local");
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
        yaml.IndexOf("- name: app", containersIndex, StringComparison.Ordinal)
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
        yaml.ShouldContain("path: /health");
        yaml.ShouldContain("port: 8080");
        yaml.ShouldContain("scheme: HTTP");
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
        yaml.ShouldContain("- cat /tmp/healthy");
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
        yaml.ShouldContain("- /bin/sh");
        yaml.ShouldContain("- -c");
        yaml.ShouldContain("- cat /tmp/healthy");
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
        yaml.ShouldContain("- sleep 5");
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
        yaml.ShouldContain("- echo started");
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
        yaml.ShouldContain("name: app-config");
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
        yaml.ShouldContain("name: config-a");
        yaml.ShouldContain("name: config-b");

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

    // === Helpers ===

    private async Task<string> GetDeploymentYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        result.ShouldContainKey("deployment.yaml");
        return Encoding.UTF8.GetString(result["deployment.yaml"]);
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
