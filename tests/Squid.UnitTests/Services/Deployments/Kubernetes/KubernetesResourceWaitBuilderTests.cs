using System.Collections.Generic;
using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesResourceWaitBuilderTests
{
    private static DeploymentActionDto CreateAction(bool statusCheckEnabled = true, string timeout = null)
    {
        var action = new DeploymentActionDto
        {
            Id = 1,
            Name = "test",
            ActionType = "Squid.KubernetesDeployRawYaml",
            Properties = new List<DeploymentActionPropertyDto>()
        };

        if (statusCheckEnabled)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.ObjectStatusCheck",
                PropertyValue = "True"
            });
        }

        if (timeout != null)
        {
            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Squid.Action.KubernetesContainers.ObjectStatusCheckTimeout",
                PropertyValue = timeout
            });
        }

        return action;
    }

    private static Dictionary<string, byte[]> MakeFiles(string yaml, string fileName = "deploy.yaml")
        => new() { [fileName] = Encoding.UTF8.GetBytes(yaml) };

    [Fact]
    public void BuildWaitScript_Deployment_ContainsRolloutStatus()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: my-deploy";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status \"deployment/my-deploy\" -n \"default\"");
    }

    [Fact]
    public void BuildWaitScript_StatefulSet_ContainsRolloutStatus()
    {
        var yaml = "apiVersion: apps/v1\nkind: StatefulSet\nmetadata:\n  name: my-sts";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status \"statefulset/my-sts\" -n \"default\"");
    }

    [Fact]
    public void BuildWaitScript_DaemonSet_ContainsRolloutStatus()
    {
        var yaml = "apiVersion: apps/v1\nkind: DaemonSet\nmetadata:\n  name: my-ds";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status \"daemonset/my-ds\" -n \"default\"");
    }

    [Fact]
    public void BuildWaitScript_Job_ContainsWaitForComplete()
    {
        var yaml = "apiVersion: batch/v1\nkind: Job\nmetadata:\n  name: my-job";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl wait --for=condition=complete \"job/my-job\" -n \"default\"");
    }

    [Fact]
    public void BuildWaitScript_ConfigMap_NoWaitCommand()
    {
        var yaml = "apiVersion: v1\nkind: ConfigMap\nmetadata:\n  name: my-cm";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldNotContain("rollout status");
        result.ShouldNotContain("kubectl wait");
    }

    [Fact]
    public void BuildWaitScript_Service_NoWaitCommand()
    {
        var yaml = "apiVersion: v1\nkind: Service\nmetadata:\n  name: my-svc";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldNotContain("rollout status");
        result.ShouldNotContain("kubectl wait");
    }

    [Fact]
    public void BuildWaitScript_MultipleResources_WaitsForAll()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: dep-1\n---\napiVersion: batch/v1\nkind: Job\nmetadata:\n  name: job-1";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status \"deployment/dep-1\" -n \"default\"");
        result.ShouldContain("kubectl wait --for=condition=complete \"job/job-1\" -n \"default\"");
    }

    [Fact]
    public void BuildWaitScript_CustomTimeout_UsesTimeout()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: dep";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(timeout: "600"), "default", ScriptSyntax.Bash);

        result.ShouldContain("--timeout=600s");
    }

    [Fact]
    public void BuildWaitScript_Disabled_ReturnsEmpty()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: dep";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(statusCheckEnabled: false), "default", ScriptSyntax.Bash);

        result.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Deployment", true)]
    [InlineData("ConfigMap", false)]
    [InlineData("Job", true)]
    [InlineData("StatefulSet", true)]
    [InlineData("Service", false)]
    [InlineData("Secret", false)]
    public void BuildWaitScript_KindSupport(string kind, bool shouldWait)
    {
        var yaml = $"apiVersion: v1\nkind: {kind}\nmetadata:\n  name: test-resource";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        if (shouldWait)
            result.ShouldNotBeEmpty();
        else
            result.ShouldNotContain("test-resource");
    }

    [Fact]
    public void ExtractResources_MultiDocumentYaml_ParsesAll()
    {
        var yaml = "apiVersion: v1\nkind: Service\nmetadata:\n  name: svc\n---\napiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: dep";
        var resources = KubernetesResourceWaitBuilder.ExtractResources(MakeFiles(yaml));

        resources.Count.ShouldBe(2);
        resources.ShouldContain(("Service", "svc", null));
        resources.ShouldContain(("Deployment", "dep", null));
    }

    [Fact]
    public void ExtractResources_InvalidYaml_ReturnsEmpty()
    {
        var files = new Dictionary<string, byte[]> { ["test.yaml"] = Encoding.UTF8.GetBytes("not valid yaml: {{broken") };
        var resources = KubernetesResourceWaitBuilder.ExtractResources(files);

        resources.ShouldBeEmpty();
    }
    // ========== Per-document identity (Calamari ManifestParser parity) ==========
    // Calamari's ResourceFinder/ManifestParser identifies each manifest document by
    // metadata.name + metadata.namespace (falling back to the step's implicit namespace).
    // The wait must target the namespace the document is actually applied into.

    [Theory]
    [InlineData("Deployment", "monitoring", "kubectl rollout status \"deployment/exporter\" -n \"monitoring\" --timeout=300s")]
    [InlineData("StatefulSet", "data", "kubectl rollout status \"statefulset/exporter\" -n \"data\" --timeout=300s")]
    [InlineData("DaemonSet", "kube-system", "kubectl rollout status \"daemonset/exporter\" -n \"kube-system\" --timeout=300s")]
    [InlineData("Job", "batch", "kubectl wait --for=condition=complete \"job/exporter\" -n \"batch\" --timeout=300s")]
    public void BuildWaitScript_DocumentDeclaresItsOwnNamespace_WaitsInThatNamespace(string kind, string documentNamespace, string expected)
    {
        var yaml = $"apiVersion: apps/v1\nkind: {kind}\nmetadata:\n  name: exporter\n  namespace: {documentNamespace}\n";

        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), enabled: true, 300, "step-ns", ScriptSyntax.Bash);

        result.ShouldContain(expected);
        result.ShouldNotContain("-n \"step-ns\"");
    }

    [Fact]
    public void BuildWaitScript_DocumentWithoutNamespace_FallsBackToStepNamespace()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: web\n";

        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), enabled: true, 45, "step-ns", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status \"deployment/web\" -n \"step-ns\" --timeout=45s");
    }

    [Fact]
    public void BuildWaitScript_LabelNamedNameBeforeMetadataName_UsesMetadataName()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  labels:\n    name: web\n  annotations:\n    name: annotated\n  name: web-exporter\n  namespace: monitoring\nspec:\n  template:\n    metadata:\n      name: pod-template\n";

        var resources = KubernetesResourceWaitBuilder.ExtractResources(MakeFiles(yaml));

        resources.ShouldBe(new[] { ("Deployment", "web-exporter", "monitoring") });
    }

    [Fact]
    public void BuildWaitScript_MultiDocumentCustomResourceWithMixedNamespaces_EachDocumentTargetsItsOwnNamespace()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["deployment.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: \"web\"\n  namespace: \"apps\"\n"),
            ["customresource.yaml"] = Encoding.UTF8.GetBytes("apiVersion: apps/v1\r\nkind: Deployment\r\nmetadata:\r\n  name: exporter\r\n  namespace: monitoring\r\n---\r\napiVersion: v1\r\nkind: ConfigMap\r\nmetadata:\r\n  name: settings\r\n---\r\napiVersion: batch/v1\r\nkind: Job\r\nmetadata:\r\n  name: migrate\r\n---\r\n")
        };

        var result = KubernetesResourceWaitBuilder.BuildWaitScript(files, enabled: true, 600, "apps", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status \"deployment/web\" -n \"apps\" --timeout=600s");
        result.ShouldContain("kubectl rollout status \"deployment/exporter\" -n \"monitoring\" --timeout=600s");
        result.ShouldContain("kubectl wait --for=condition=complete \"job/migrate\" -n \"apps\" --timeout=600s");
        result.ShouldNotContain("settings");
    }

    [Fact]
    public void BuildWaitScript_PowerShell_UsesDocumentNamespace()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: exporter\n  namespace: monitoring\n";

        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), enabled: true, 120, "step-ns", ScriptSyntax.PowerShell);

        result.ShouldContain("kubectl rollout status \"deployment/exporter\" -n \"monitoring\" --timeout=120s");
        result.ShouldContain("if ($LASTEXITCODE -ne 0)");
    }

    [Fact]
    public void BuildWaitScript_SameResourceInTwoFiles_WaitsOnce()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: web\n  namespace: apps\n";
        var files = new Dictionary<string, byte[]> { ["a.yaml"] = Encoding.UTF8.GetBytes(yaml), ["b.yml"] = Encoding.UTF8.GetBytes(yaml) };

        var result = KubernetesResourceWaitBuilder.BuildWaitScript(files, enabled: true, 300, "apps", ScriptSyntax.Bash);

        result.Split("kubectl rollout status").Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: #{Unresolved}\n")]
    [InlineData("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: \"web; rm -rf /\"\n")]
    [InlineData("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: web\n  namespace: \"$(whoami)\"\n")]
    [InlineData("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: |\n    multi\n    line\n")]
    [InlineData("apiVersion: apps/v1\nkind: Deployment\nspec:\n  replicas: 1\n")]
    [InlineData("- just\n- a list\n")]
    [InlineData("# only a comment\n")]
    [InlineData("apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: a\n  name: b\n")]
    [InlineData("not valid yaml: {{broken")]
    public void BuildWaitScript_DocumentWithoutAUsableIdentity_IsSkipped(string yaml)
    {
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), enabled: true, 300, "apps", ScriptSyntax.Bash);

        result.ShouldNotContain("kubectl rollout status");
    }

    [Fact]
    public void ExtractResources_NonYamlFiles_AreIgnored()
    {
        var yaml = "apiVersion: apps/v1\nkind: Deployment\nmetadata:\n  name: web\n";
        var files = new Dictionary<string, byte[]> { ["bluegreen-switch.sh"] = Encoding.UTF8.GetBytes(yaml) };

        KubernetesResourceWaitBuilder.ExtractResources(files).ShouldBeEmpty();
    }
}
