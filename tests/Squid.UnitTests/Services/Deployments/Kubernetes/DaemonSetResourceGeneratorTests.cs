using System.Collections.Generic;
using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class DaemonSetResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    [Fact]
    public async Task Generate_DaemonSet_CorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal("DaemonSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("daemonset.yaml");
        var yaml = Encoding.UTF8.GetString(result["daemonset.yaml"]);

        yaml.ShouldContain("apiVersion: apps/v1");
        yaml.ShouldContain("kind: DaemonSet");
    }

    [Fact]
    public async Task Generate_DaemonSet_NoReplicasField()
    {
        var (step, action) = CreateMinimal("DaemonSet");
        Add(action, "Squid.Action.KubernetesContainers.Replicas", "3");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["daemonset.yaml"]);

        yaml.ShouldNotContain("replicas:");
    }

    [Fact]
    public async Task Generate_DaemonSet_NoStrategyBlock()
    {
        var (step, action) = CreateMinimal("DaemonSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["daemonset.yaml"]);

        yaml.ShouldNotContain("strategy:");
    }

    [Fact]
    public async Task Generate_DaemonSet_HasSelectorAndTemplate()
    {
        var (step, action) = CreateMinimal("DaemonSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["daemonset.yaml"]);

        yaml.ShouldContain("selector:");
        yaml.ShouldContain("matchLabels:");
        yaml.ShouldContain("template:");
        yaml.ShouldContain("containers:");
    }

    [Fact]
    public async Task Generate_DaemonSet_DoesNotGenerateDeploymentYaml()
    {
        var (step, action) = CreateMinimal("DaemonSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("deployment.yaml");
    }

    [Fact]
    public void CanGenerate_WrongResourceType_ReturnsFalse()
    {
        var generator = new DaemonSetResourceGenerator();
        var properties = new Dictionary<string, string>
        {
            ["Squid.Action.KubernetesContainers.DeploymentResourceType"] = "Deployment",
            ["Squid.Action.KubernetesContainers.Containers"] = """[{"Name":"app","Image":"nginx"}]"""
        };

        generator.CanGenerate(properties).ShouldBeFalse();
    }

    [Fact]
    public void CanGenerate_CorrectResourceType_ReturnsTrue()
    {
        var generator = new DaemonSetResourceGenerator();
        var properties = new Dictionary<string, string>
        {
            ["Squid.Action.KubernetesContainers.DeploymentResourceType"] = "DaemonSet",
            ["Squid.Action.KubernetesContainers.Containers"] = """[{"Name":"app","Image":"nginx","Ports":[{"key":"http","value":"80"}]}]"""
        };

        generator.CanGenerate(properties).ShouldBeTrue();
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal(string resourceType)
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };

        Add(action, "Squid.Action.KubernetesContainers.DeploymentName", "minimal-deploy");
        Add(action, "Squid.Action.KubernetesContainers.DeploymentResourceType", resourceType);
        Add(action, "Squid.Action.KubernetesContainers.Containers", """[{"Name":"app","Image":"nginx:latest","Ports":[{"key":"http","value":"80","option":"TCP"}]}]""");

        return (step, action);
    }

    private static void Add(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto { PropertyName = name, PropertyValue = value });
    }
}
