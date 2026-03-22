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

        result.ShouldContain("kubectl rollout status deployment/my-deploy");
    }

    [Fact]
    public void BuildWaitScript_StatefulSet_ContainsRolloutStatus()
    {
        var yaml = "apiVersion: apps/v1\nkind: StatefulSet\nmetadata:\n  name: my-sts";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status statefulset/my-sts");
    }

    [Fact]
    public void BuildWaitScript_DaemonSet_ContainsRolloutStatus()
    {
        var yaml = "apiVersion: apps/v1\nkind: DaemonSet\nmetadata:\n  name: my-ds";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl rollout status daemonset/my-ds");
    }

    [Fact]
    public void BuildWaitScript_Job_ContainsWaitForComplete()
    {
        var yaml = "apiVersion: batch/v1\nkind: Job\nmetadata:\n  name: my-job";
        var result = KubernetesResourceWaitBuilder.BuildWaitScript(MakeFiles(yaml), CreateAction(), "default", ScriptSyntax.Bash);

        result.ShouldContain("kubectl wait --for=condition=complete job/my-job");
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

        result.ShouldContain("kubectl rollout status deployment/dep-1");
        result.ShouldContain("kubectl wait --for=condition=complete job/job-1");
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
        resources.ShouldContain(("Service", "svc"));
        resources.ShouldContain(("Deployment", "dep"));
    }

    [Fact]
    public void ExtractResources_InvalidYaml_ReturnsEmpty()
    {
        var files = new Dictionary<string, byte[]> { ["test.yaml"] = Encoding.UTF8.GetBytes("not valid yaml: {{broken") };
        var resources = KubernetesResourceWaitBuilder.ExtractResources(files);

        resources.ShouldBeEmpty();
    }
}
