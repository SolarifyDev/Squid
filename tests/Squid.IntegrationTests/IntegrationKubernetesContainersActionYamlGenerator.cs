using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.IntegrationTests;

public class IntegrationKubernetesContainersActionYamlGenerator : IntegrationTestBase
{
    public IntegrationKubernetesContainersActionYamlGenerator(IntegrationFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task ShouldGenerateFrontendDeploymentServiceAndConfigMapYaml()
    {
        var result = await Run<KubernetesContainersActionYamlGenerator, Dictionary<string, byte[]>>(async generator =>
        {
            var step = new DeploymentStepDto
            {
                Id = 1,
                Name = "Deploy frontend",
                StepType = "Deploy"
            };

            var action = new DeploymentActionDto
            {
                Id = 1,
                StepId = step.Id,
                Name = "Deploy postboyweb",
                ActionType = "Octopus.KubernetesDeployContainers"
            };

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.DeploymentName",
                PropertyValue = "postboyweb-deployment"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.Namespace",
                PropertyValue = "postboyweb-namespace"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.Replicas",
                PropertyValue = "1"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.DeploymentStyle",
                PropertyValue = "RollingUpdate"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ServiceName",
                PropertyValue = "postboyweb-service"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ServiceType",
                PropertyValue = "ClusterIP"
            });

            var servicePortsJson = "[{\"name\":\"http\",\"port\":\"8000\",\"targetPort\":\"80\",\"nodePort\":\"\",\"protocol\":\"TCP\"}]";

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ServicePorts",
                PropertyValue = servicePortsJson
            });

            var containersJson = """
[
  {
    "Name": "postboyweb",
    "Image": "nginx:1.25",
    "Ports": [
      { "key": "http", "value": "80", "option": "TCP" }
    ],
    "Resources": {
      "requests": { "cpu": "100m", "memory": "256Mi" },
      "limits": { "cpu": "500m", "memory": "512Mi" }
    },
    "VolumeMounts": [
      { "key": "postboyweb-config-volume", "value": "/app/appsettings.json", "option": "appsettings.json" }
    ]
  }
]
""";

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.Containers",
                PropertyValue = containersJson
            });

            var combinedVolumesJson = """
[
  {
    "Name": "postboyweb-config-volume",
    "Type": "ConfigMap",
    "ReferenceName": "postboyweb-config"
  }
]
""";

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.CombinedVolumes",
                PropertyValue = combinedVolumesJson
            });

            var configValues = new Dictionary<string, string>
            {
                ["appsettings.json"] = "{ \"ApiBaseUrl\": \"https://api.example.com\" }"
            };

            var configValuesJson = JsonSerializer.Serialize(configValues);

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ConfigMapName",
                PropertyValue = "postboyweb-config"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ConfigMapValues",
                PropertyValue = configValuesJson
            });

            var yamlFiles = await generator.GenerateAsync(step, action, CancellationToken.None).ConfigureAwait(false);

            yamlFiles.ShouldNotBeNull();

            yamlFiles.Count.ShouldBe(3);

            return yamlFiles;
        }).ConfigureAwait(false);

        result.ShouldContainKey("deployment.yaml");
        result.ShouldContainKey("service.yaml");
        result.ShouldContainKey("configmap.yaml");

        var deploymentYaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        var serviceYaml = Encoding.UTF8.GetString(result["service.yaml"]);
        var configMapYaml = Encoding.UTF8.GetString(result["configmap.yaml"]);

        deploymentYaml.ShouldContain("name: postboyweb-deployment");
        deploymentYaml.ShouldContain("namespace: postboyweb-namespace");
        deploymentYaml.ShouldContain("replicas: 1");
        deploymentYaml.ShouldContain("image: nginx:1.25");
        deploymentYaml.ShouldContain("containerPort: 80");
        deploymentYaml.ShouldContain("volumeMounts:");

        serviceYaml.ShouldContain("kind: Service");
        serviceYaml.ShouldContain("name: postboyweb-service");
        serviceYaml.ShouldContain("type: ClusterIP");
        serviceYaml.ShouldContain("port: 8000");
        serviceYaml.ShouldContain("targetPort: 80");

        configMapYaml.ShouldContain("kind: ConfigMap");
        configMapYaml.ShouldContain("name: postboyweb-config");
        configMapYaml.ShouldContain("appsettings.json");
        configMapYaml.ShouldContain("ApiBaseUrl");
    }

    [Fact]
    public async Task ShouldGenerateBackendDeploymentServiceAndConfigMapYaml()
    {
        var result = await Run<KubernetesContainersActionYamlGenerator, Dictionary<string, byte[]>>(async generator =>
        {
            var step = new DeploymentStepDto
            {
                Id = 2,
                Name = "Deploy backend",
                StepType = "Deploy"
            };

            var action = new DeploymentActionDto
            {
                Id = 2,
                StepId = step.Id,
                Name = "Deploy squid-api",
                ActionType = "Octopus.KubernetesDeployContainers"
            };

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.DeploymentName",
                PropertyValue = "squid-api-deployment"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.Namespace",
                PropertyValue = "squid-backend-namespace"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.Replicas",
                PropertyValue = "2"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.DeploymentStyle",
                PropertyValue = "RollingUpdate"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ServiceName",
                PropertyValue = "squid-api-service"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ServiceType",
                PropertyValue = "ClusterIP"
            });

            var servicePortsJson = "[{\"name\":\"http\",\"port\":\"8080\",\"targetPort\":\"8080\",\"nodePort\":\"\",\"protocol\":\"TCP\"}]";

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ServicePorts",
                PropertyValue = servicePortsJson
            });

            var containersJson = """
[
  {
    "Name": "squid-api",
    "Image": "repo/squid-api:1.0",
    "Ports": [
      { "key": "http", "value": "8080", "option": "TCP" }
    ],
    "Resources": {
      "requests": { "cpu": "#{CpuRequests}", "memory": "#{MemoryRequests}" },
      "limits": { "cpu": "#{CpuLimits}", "memory": "#{MemoryLimits}" }
    },
    "ConfigMapEnvFromSource": [
      { "key": "squid-api-config-variables" }
    ]
  }
]
""";

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.Containers",
                PropertyValue = containersJson
            });

            var configValues = new Dictionary<string, string>
            {
                ["ConnectionStrings__DefaultConnection"] = "Host=example;Database=example;User Id=example;Password=example;",
                ["Serilog__MinimumLevel__Default"] = "Information"
            };

            var configValuesJson = JsonSerializer.Serialize(configValues);

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ConfigMapName",
                PropertyValue = "squid-api-config-variables"
            });

            action.Properties.Add(new DeploymentActionPropertyDto
            {
                PropertyName = "Octopus.Action.KubernetesContainers.ConfigMapValues",
                PropertyValue = configValuesJson
            });

            var yamlFiles = await generator.GenerateAsync(step, action, CancellationToken.None).ConfigureAwait(false);

            yamlFiles.ShouldNotBeNull();

            yamlFiles.Count.ShouldBe(3);

            return yamlFiles;
        }).ConfigureAwait(false);

        result.ShouldContainKey("deployment.yaml");
        result.ShouldContainKey("service.yaml");
        result.ShouldContainKey("configmap.yaml");

        var deploymentYaml = Encoding.UTF8.GetString(result["deployment.yaml"]);
        var serviceYaml = Encoding.UTF8.GetString(result["service.yaml"]);
        var configMapYaml = Encoding.UTF8.GetString(result["configmap.yaml"]);

        deploymentYaml.ShouldContain("name: squid-api-deployment");
        deploymentYaml.ShouldContain("namespace: squid-backend-namespace");
        deploymentYaml.ShouldContain("replicas: 2");
        deploymentYaml.ShouldContain("image: repo/squid-api:1.0");
        deploymentYaml.ShouldContain("containerPort: 8080");
        deploymentYaml.ShouldContain("envFrom:");
        deploymentYaml.ShouldContain("squid-api-config-variables");

        serviceYaml.ShouldContain("kind: Service");
        serviceYaml.ShouldContain("name: squid-api-service");
        serviceYaml.ShouldContain("type: ClusterIP");
        serviceYaml.ShouldContain("port: 8080");
        serviceYaml.ShouldContain("targetPort: 8080");

        configMapYaml.ShouldContain("kind: ConfigMap");
        configMapYaml.ShouldContain("name: squid-api-config-variables");
        configMapYaml.ShouldContain("ConnectionStrings__DefaultConnection");
        configMapYaml.ShouldContain("Host=example;Database=example;User Id=example;Password=example;");
    }

}

