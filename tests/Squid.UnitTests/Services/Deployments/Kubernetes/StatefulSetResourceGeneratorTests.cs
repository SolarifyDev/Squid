using System.Collections.Generic;
using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class StatefulSetResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    [Fact]
    public async Task Generate_StatefulSet_CorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal("StatefulSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("statefulset.yaml");
        var yaml = Encoding.UTF8.GetString(result["statefulset.yaml"]);

        yaml.ShouldContain("apiVersion: apps/v1");
        yaml.ShouldContain("kind: StatefulSet");
    }

    [Fact]
    public async Task Generate_StatefulSet_IncludesServiceName()
    {
        var (step, action) = CreateMinimal("StatefulSet");
        Add(action, "Squid.Action.KubernetesContainers.ServiceName", "my-headless-svc");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["statefulset.yaml"]);

        yaml.ShouldContain("serviceName: \"my-headless-svc\"");
    }

    [Fact]
    public async Task Generate_StatefulSet_NoServiceName_FallsBackToDeploymentName()
    {
        var (step, action) = CreateMinimal("StatefulSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["statefulset.yaml"]);

        yaml.ShouldContain("serviceName: \"minimal-deploy\"");
    }

    [Fact]
    public async Task Generate_StatefulSet_IncludesReplicas()
    {
        var (step, action) = CreateMinimal("StatefulSet");
        Add(action, "Squid.Action.KubernetesContainers.Replicas", "3");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["statefulset.yaml"]);

        yaml.ShouldContain("replicas: 3");
    }

    [Fact]
    public async Task Generate_StatefulSet_NoStrategyBlock()
    {
        var (step, action) = CreateMinimal("StatefulSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["statefulset.yaml"]);

        yaml.ShouldNotContain("strategy:");
    }

    [Fact]
    public async Task Generate_StatefulSet_HasSelectorAndTemplate()
    {
        var (step, action) = CreateMinimal("StatefulSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["statefulset.yaml"]);

        yaml.ShouldContain("selector:");
        yaml.ShouldContain("matchLabels:");
        yaml.ShouldContain("template:");
        yaml.ShouldContain("containers:");
    }

    [Fact]
    public async Task Generate_StatefulSet_DoesNotGenerateDeploymentYaml()
    {
        var (step, action) = CreateMinimal("StatefulSet");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("deployment.yaml");
    }

    [Fact]
    public void CanGenerate_WrongResourceType_ReturnsFalse()
    {
        var generator = new StatefulSetResourceGenerator();
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
        var generator = new StatefulSetResourceGenerator();
        var properties = new Dictionary<string, string>
        {
            ["Squid.Action.KubernetesContainers.DeploymentResourceType"] = "StatefulSet",
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
