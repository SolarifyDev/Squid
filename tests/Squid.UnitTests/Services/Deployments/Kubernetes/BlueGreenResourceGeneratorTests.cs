using System.Text;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class BlueGreenResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    [Theory]
    [InlineData(null, "green")]
    [InlineData("", "green")]
    [InlineData("blue", "green")]
    [InlineData("green", "blue")]
    public void ResolveNewSlot_AlternatesCorrectly(string? currentSlot, string expectedSlot)
    {
        BlueGreenResourceGenerator.ResolveNewSlot(currentSlot).ShouldBe(expectedSlot);
    }

    [Fact]
    public async Task Generate_BlueGreen_CreatesVersionedDeployment()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("deployment.yaml");
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("name: test-deploy-green");
    }

    [Fact]
    public async Task Generate_BlueGreen_IncludesSlotLabel()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("squid.io/deployment-slot: green");
    }

    [Fact]
    public async Task Generate_BlueGreen_WithActiveBlueSlot_CreatesGreenDeployment()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");
        Add(action, "Squid.Action.KubernetesContainers.BlueGreenActiveSlot", "blue");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("name: test-deploy-green");
        yaml.ShouldContain("squid.io/deployment-slot: green");
    }

    [Fact]
    public async Task Generate_BlueGreen_WithActiveGreenSlot_CreatesBlueDeployment()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");
        Add(action, "Squid.Action.KubernetesContainers.BlueGreenActiveSlot", "green");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("name: test-deploy-blue");
        yaml.ShouldContain("squid.io/deployment-slot: blue");
    }

    [Fact]
    public async Task Generate_BlueGreen_GeneratesSwitchScript()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("bluegreen-switch.sh");
        var script = Encoding.UTF8.GetString(result["bluegreen-switch.sh"]);
        script.ShouldContain("kubectl patch service");
        script.ShouldContain("green");
    }

    [Fact]
    public async Task Generate_BlueGreen_GeneratesScaleDownScript()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("bluegreen-scaledown.sh");
        var script = Encoding.UTF8.GetString(result["bluegreen-scaledown.sh"]);
        script.ShouldContain("kubectl scale deployment test-deploy-blue");
        script.ShouldContain("--replicas=0");
    }

    [Fact]
    public async Task Generate_BlueGreen_StillGeneratesServiceAndConfigMap()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "BlueGreen");
        Add(action, "Squid.Action.KubernetesContainers.ServiceName", "test-svc");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","protocol":"TCP"}]""");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("service.yaml");
        result.ShouldContainKey("deployment.yaml");
    }

    [Fact]
    public async Task Generate_NonBlueGreen_DoesNotGenerateBlueGreenFiles()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("deployment.yaml");
        result.ShouldNotContainKey("bluegreen-switch.sh");
        result.ShouldNotContainKey("bluegreen-scaledown.sh");

        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        yaml.ShouldContain("name: test-deploy");
        yaml.ShouldNotContain("test-deploy-green");
        yaml.ShouldNotContain("test-deploy-blue");
    }

    [Fact]
    public void GenerateSwitchScript_IncludesNamespace()
    {
        var script = BlueGreenResourceGenerator.GenerateSwitchScript("my-svc", "production", "green");

        script.ShouldContain("-n production");
        script.ShouldContain("kubectl patch service my-svc");
    }

    [Fact]
    public void GenerateScaleDownScript_IncludesNamespace()
    {
        var script = BlueGreenResourceGenerator.GenerateScaleDownScript("my-deploy-blue", "production");

        script.ShouldContain("-n production");
        script.ShouldContain("kubectl scale deployment my-deploy-blue");
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
