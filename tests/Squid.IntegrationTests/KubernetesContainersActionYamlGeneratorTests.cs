using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Shouldly;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.IntegrationTests.Builders;
using Squid.IntegrationTests.Fixtures;
using Xunit;

namespace Squid.IntegrationTests;

[Collection("Sequential")]
public class KubernetesContainersActionYamlGeneratorTests : TestBase<KubernetesContainersActionYamlGeneratorTests>
{
    [Fact]
    public async Task GenerateAsync_ShouldCreateDeploymentServiceAndConfigMap()
    {
        var generator = Resolve<KubernetesContainersActionYamlGenerator>();

        var testData = new KubernetesTestDataBuilder()
            .WithDeploymentName("myapp")
            .WithNamespace("production")
            .WithReplicas(3)
            .WithService("myapp-service")
            .WithContainer("myapp", "myregistry/myapp:v1.0", 8080);

        var yamlFiles = await testData.GenerateYamlAsync(generator);

        yamlFiles.Count.ShouldBe(3);
        yamlFiles.ShouldContainKey("deployment.yaml");
        yamlFiles.ShouldContainKey("service.yaml");
        yamlFiles.ShouldContainKey("configmap.yaml");

        var deploymentYaml = Encoding.UTF8.GetString(yamlFiles["deployment.yaml"]);
        deploymentYaml.ShouldContain("name: myapp");
        deploymentYaml.ShouldContain("replicas: 3");
        deploymentYaml.ShouldContain("image: myregistry/myapp:v1.0");
    }

    [Fact]
    public async Task GenerateAsync_ShouldGenerateCorrectServiceYaml()
    {
        var generator = Resolve<KubernetesContainersActionYamlGenerator>();

        var servicePortsJson = JsonSerializer.Serialize(new[]
        {
            new { name = "http", port = "8000", targetPort = "80", nodePort = "", protocol = "TCP" }
        });

        var testData = new KubernetesTestDataBuilder()
            .WithDeploymentName("frontend")
            .WithNamespace("web")
            .WithReplicas(2)
            .WithDeploymentStyle("RollingUpdate")
            .WithService("frontend-service", "ClusterIP", servicePortsJson)
            .WithContainer("frontend", "nginx:1.25", 80);

        var yamlFiles = await testData.GenerateYamlAsync(generator);

        var serviceYaml = Encoding.UTF8.GetString(yamlFiles["service.yaml"]);
        serviceYaml.ShouldContain("kind: Service");
        serviceYaml.ShouldContain("name: frontend-service");
        serviceYaml.ShouldContain("type: ClusterIP");
        serviceYaml.ShouldContain("port: 8000");
        serviceYaml.ShouldContain("targetPort: 80");
    }

    [Fact]
    public async Task GenerateAsync_ShouldGenerateConfigMapWithValues()
    {
        var generator = Resolve<KubernetesContainersActionYamlGenerator>();

        var testData = new KubernetesTestDataBuilder()
            .WithDeploymentName("api")
            .WithNamespace("backend")
            .WithReplicas(1)
            .WithService("api-service")
            .WithContainer("api", "myrepo/api:v2.0", 8080)
            .WithConfigMap("api-config", new Dictionary<string, string>
            {
                ["ConnectionStrings__DefaultConnection"] = "Host=localhost;Database=mydb",
                ["ApiKey"] = "secret-key-123"
            });

        var yamlFiles = await testData.GenerateYamlAsync(generator);

        var configMapYaml = Encoding.UTF8.GetString(yamlFiles["configmap.yaml"]);
        configMapYaml.ShouldContain("kind: ConfigMap");
        configMapYaml.ShouldContain("name: api-config");
        configMapYaml.ShouldContain("ConnectionStrings__DefaultConnection");
        configMapYaml.ShouldContain("Host=localhost;Database=mydb");
    }

    [Fact]
    public async Task GenerateAsync_ShouldGenerateDeploymentWithResources()
    {
        var generator = Resolve<KubernetesContainersActionYamlGenerator>();

        var testData = new KubernetesTestDataBuilder()
            .WithDeploymentName("worker")
            .WithNamespace("workers")
            .WithReplicas(4)
            .WithService("worker-service")
            .WithContainer("worker", "myrepo/worker:latest", 5000);

        var yamlFiles = await testData.GenerateYamlAsync(generator);

        var deploymentYaml = Encoding.UTF8.GetString(yamlFiles["deployment.yaml"]);
        deploymentYaml.ShouldContain("namespace: workers");
        deploymentYaml.ShouldContain("replicas: 4");
        deploymentYaml.ShouldContain("image: myrepo/worker:latest");
        deploymentYaml.ShouldContain("containerPort: 5000");
    }

    [Fact]
    public async Task GenerateAsync_ShouldGenerateAllRequiredYamlFiles()
    {
        var generator = Resolve<KubernetesContainersActionYamlGenerator>();

        var testData = new KubernetesTestDataBuilder()
            .WithDeploymentName("webapp")
            .WithNamespace("default")
            .WithReplicas(1)
            .WithDeploymentStyle("RollingUpdate")
            .WithService("webapp-service", "LoadBalancer")
            .WithContainer("web", "nginx:1.21", 80)
            .WithConfigMap("webapp-config", new Dictionary<string, string>
            {
                ["appsettings.json"] = "{ \"Server\": \"localhost\" }"
            });

        var yamlFiles = await testData.GenerateYamlAsync(generator);

        yamlFiles.Count.ShouldBe(3);
        
        foreach (var (fileName, content) in yamlFiles)
        {
            var yaml = Encoding.UTF8.GetString(content);
            yaml.ShouldNotBeNullOrEmpty();
            yaml.ShouldContain("apiVersion:");
            yaml.ShouldContain("kind:");
        }

        var deploymentContent = Encoding.UTF8.GetString(yamlFiles["deployment.yaml"]);
        deploymentContent.ShouldContain("kind: Deployment");
        deploymentContent.ShouldContain("name: webapp");

        var serviceContent = Encoding.UTF8.GetString(yamlFiles["service.yaml"]);
        serviceContent.ShouldContain("kind: Service");
        serviceContent.ShouldContain("name: webapp-service");
        serviceContent.ShouldContain("type: LoadBalancer");

        var configMapContent = Encoding.UTF8.GetString(yamlFiles["configmap.yaml"]);
        configMapContent.ShouldContain("kind: ConfigMap");
        configMapContent.ShouldContain("name: webapp-config");
    }
}
