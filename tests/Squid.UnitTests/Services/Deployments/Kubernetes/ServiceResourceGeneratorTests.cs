using System.Text;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class ServiceResourceGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _compositor = new();

    // === Kind / apiVersion ===

    [Fact]
    public async Task Generate_BasicService_HasCorrectApiVersionAndKind()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("apiVersion: v1");
        yaml.ShouldContain("kind: Service");
    }

    // === Metadata ===

    [Fact]
    public async Task Generate_ServiceName_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("name: test-service");
    }

    [Fact]
    public async Task Generate_ServiceNamespace_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.Namespace", "prod-ns");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("namespace: prod-ns");
    }

    [Fact]
    public async Task Generate_ServiceWithAnnotations_IncludesAnnotationsBlock()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ServiceAnnotations",
            """[{"Key":"service.beta.kubernetes.io/aws-load-balancer-type","Value":"external"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("service.beta.kubernetes.io/aws-load-balancer-type: external");
    }

    // === Spec: type ===

    [Theory]
    [InlineData("ClusterIP")]
    [InlineData("NodePort")]
    [InlineData("LoadBalancer")]
    public async Task Generate_ServiceType_IsIncluded(string serviceType)
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ServiceType", serviceType);

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain($"type: {serviceType}");
    }

    [Fact]
    public async Task Generate_WithClusterIp_IncludesClusterIpField()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.ServiceClusterIp", "10.96.0.1");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("clusterIP: 10.96.0.1");
    }

    [Fact]
    public async Task Generate_NoClusterIp_ClusterIpFieldOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldNotContain("clusterIP:");
    }

    // === Selector ===

    [Fact]
    public async Task Generate_NoDeploymentLabels_UsesDeploymentNameAsAppSelector()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("selector:");
        yaml.ShouldContain("app: test-app");
    }

    [Fact]
    public async Task Generate_WithDeploymentLabels_UsesLabelsAsSelector()
    {
        var (step, action) = CreateMinimal();
        Add(action, "Squid.Action.KubernetesContainers.DeploymentLabels",
            """[{"Key":"app","Value":"my-app"},{"Key":"tier","Value":"frontend"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("app: my-app");
        yaml.ShouldContain("tier: frontend");
    }

    // === Ports ===

    [Fact]
    public async Task Generate_Port_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("ports:");
        yaml.ShouldContain("port: 80");
    }

    [Fact]
    public async Task Generate_PortName_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("name: http");
    }

    [Fact]
    public async Task Generate_TargetPort_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("targetPort: 8080");
    }

    [Fact]
    public async Task Generate_EmptyTargetPort_TargetPortOmitted()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.ServicePorts");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"","nodePort":"","protocol":"TCP"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldNotContain("targetPort:");
    }

    [Fact]
    public async Task Generate_NodePort_IsIncluded()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.ServicePorts");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"8080","nodePort":"30080","protocol":"TCP"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("nodePort: 30080");
    }

    [Fact]
    public async Task Generate_EmptyNodePort_NodePortOmitted()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldNotContain("nodePort:");
    }

    [Fact]
    public async Task Generate_Protocol_IsIncluded()
    {
        var (step, action) = CreateMinimal();

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("protocol: TCP");
    }

    [Fact]
    public async Task Generate_MultiplePorts_AllPortsGenerated()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.ServicePorts");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"8080","nodePort":"","protocol":"TCP"},{"name":"https","port":"443","targetPort":"8443","nodePort":"","protocol":"TCP"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("port: 80");
        yaml.ShouldContain("port: 443");
        yaml.ShouldContain("name: https");
        yaml.ShouldContain("targetPort: 8443");
    }

    [Fact]
    public async Task Generate_NumericPortJsonValue_ParsedCorrectly()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.ServicePorts");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":80,"targetPort":8080,"nodePort":"","protocol":"TCP"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("port: 80");
        yaml.ShouldContain("targetPort: 8080");
    }

    [Fact]
    public async Task Generate_NamedTargetPort_WrittenAsString()
    {
        var (step, action) = CreateMinimal();
        action.Properties.RemoveAll(p => p.PropertyName == "Squid.Action.KubernetesContainers.ServicePorts");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"http-port","nodePort":"","protocol":"TCP"}]""");

        var yaml = await GetServiceYaml(step, action);

        yaml.ShouldContain("targetPort: http-port");
    }

    // === CanGenerate guards ===

    [Fact]
    public async Task Generate_NoServiceName_ServiceYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"8080","nodePort":"","protocol":"TCP"}]""");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("service.yaml");
    }

    [Fact]
    public async Task Generate_NoPorts_ServiceYamlNotGenerated()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers", Name = "test" };
        Add(action, "Squid.Action.KubernetesContainers.ServiceName", "my-service");

        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("service.yaml");
    }

    // === Helpers ===

    private async Task<string> GetServiceYaml(DeploymentStepDto step, DeploymentActionDto action)
    {
        var result = await _compositor.GenerateAsync(step, action, CancellationToken.None);
        result.ShouldContainKey("service.yaml");
        return Encoding.UTF8.GetString(result["service.yaml"]);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimal()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "test-deploy"
        };

        Add(action, "Squid.Action.KubernetesContainers.DeploymentName", "test-app");
        Add(action, "Squid.Action.KubernetesContainers.ServiceName", "test-service");
        Add(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"8080","nodePort":"","protocol":"TCP"}]""");

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
