using System.Collections.Generic;
using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class JobResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    [Fact]
    public async Task Generate_Job_CorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal("Job");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("job.yaml");
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        yaml.ShouldContain("apiVersion: batch/v1");
        yaml.ShouldContain("kind: Job");
    }

    [Fact]
    public async Task Generate_Job_RestartPolicyDefaultsToNever()
    {
        var (step, action) = CreateMinimal("Job");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        yaml.ShouldContain("restartPolicy: \"Never\"");
    }

    [Fact]
    public async Task Generate_Job_ReplicasMapsToCompletions()
    {
        var (step, action) = CreateMinimal("Job");
        Add(action, "Squid.Action.KubernetesContainers.Replicas", "5");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        yaml.ShouldContain("completions: 5");
        yaml.ShouldNotContain("replicas:");
    }

    [Fact]
    public async Task Generate_Job_IncludesBackoffLimit()
    {
        var (step, action) = CreateMinimal("Job");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        yaml.ShouldContain("backoffLimit: 6");
    }

    [Fact]
    public async Task Generate_Job_NoStrategyBlock()
    {
        var (step, action) = CreateMinimal("Job");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        yaml.ShouldNotContain("strategy:");
    }

    [Fact]
    public async Task Generate_Job_HasSelectorAndTemplate()
    {
        var (step, action) = CreateMinimal("Job");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        yaml.ShouldContain("selector:");
        yaml.ShouldContain("matchLabels:");
        yaml.ShouldContain("template:");
        yaml.ShouldContain("containers:");
    }

    [Fact]
    public async Task Generate_Job_DoesNotGenerateDeploymentYaml()
    {
        var (step, action) = CreateMinimal("Job");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("deployment.yaml");
    }

    [Fact]
    public void CanGenerate_WrongResourceType_ReturnsFalse()
    {
        var generator = new JobResourceGenerator();
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
        var generator = new JobResourceGenerator();
        var properties = new Dictionary<string, string>
        {
            ["Squid.Action.KubernetesContainers.DeploymentResourceType"] = "Job",
            ["Squid.Action.KubernetesContainers.Containers"] = """[{"Name":"app","Image":"nginx","Ports":[{"key":"http","value":"80"}]}]"""
        };

        generator.CanGenerate(properties).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task Generate_Job_Replicas_ZeroHandled(bool setReplicas, int expectedCompletions)
    {
        var (step, action) = CreateMinimal("Job");

        if (setReplicas)
            Add(action, "Squid.Action.KubernetesContainers.Replicas", "0");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["job.yaml"]);

        if (setReplicas)
            yaml.ShouldContain($"completions: {expectedCompletions}");
        else
            yaml.ShouldNotContain("completions:");
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
