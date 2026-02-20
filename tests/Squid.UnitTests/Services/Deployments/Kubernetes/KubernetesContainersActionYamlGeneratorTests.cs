using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class KubernetesContainersActionYamlGeneratorTests
{
    private readonly KubernetesContainersActionYamlGenerator _generator = new();

    // === CanHandle ===

    [Fact]
    public void CanHandle_CorrectActionType_ReturnsTrue()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        _generator.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_CaseInsensitive_ReturnsTrue()
    {
        var action = new DeploymentActionDto { ActionType = "squid.kubernetesdeploycontainers" };
        _generator.CanHandle(action).ShouldBeTrue();
    }

    [Fact]
    public void CanHandle_WrongActionType_ReturnsFalse()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesRunScript" };
        _generator.CanHandle(action).ShouldBeFalse();
    }

    [Fact]
    public void CanHandle_NullAction_ReturnsFalse()
    {
        _generator.CanHandle(null).ShouldBeFalse();
    }

    // === GenerateAsync — Frontend scenario (Deployment + Service + ConfigMap with volumes) ===

    [Fact]
    public async Task GenerateAsync_FrontendWithVolumes_GeneratesThreeYamlFiles()
    {
        var (step, action) = CreateFrontendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(3);
        result.ShouldContainKey("deployment.yaml");
        result.ShouldContainKey("service.yaml");
        result.ShouldContainKey("configmap.yaml");
    }

    [Fact]
    public async Task GenerateAsync_FrontendDeploymentYaml_ContainsExpectedFields()
    {
        var (step, action) = CreateFrontendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("name: postboyweb-deployment");
        yaml.ShouldContain("namespace: postboyweb-namespace");
        yaml.ShouldContain("replicas: 1");
        yaml.ShouldContain("image: nginx:1.25");
        yaml.ShouldContain("containerPort: 80");
        yaml.ShouldContain("volumeMounts:");
    }

    [Fact]
    public async Task GenerateAsync_FrontendServiceYaml_ContainsExpectedFields()
    {
        var (step, action) = CreateFrontendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["service.yaml"]);

        yaml.ShouldContain("kind: Service");
        yaml.ShouldContain("name: postboyweb-service");
        yaml.ShouldContain("type: ClusterIP");
        yaml.ShouldContain("port: 8000");
        yaml.ShouldContain("targetPort: 80");
    }

    [Fact]
    public async Task GenerateAsync_FrontendConfigMapYaml_ContainsExpectedFields()
    {
        var (step, action) = CreateFrontendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["configmap.yaml"]);

        yaml.ShouldContain("kind: ConfigMap");
        yaml.ShouldContain("name: postboyweb-config");
        yaml.ShouldContain("appsettings.json");
        yaml.ShouldContain("ApiBaseUrl");
    }

    // === GenerateAsync — Backend scenario (envFrom + no volumes) ===

    [Fact]
    public async Task GenerateAsync_BackendWithEnvFrom_GeneratesThreeYamlFiles()
    {
        var (step, action) = CreateBackendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(3);
        result.ShouldContainKey("deployment.yaml");
        result.ShouldContainKey("service.yaml");
        result.ShouldContainKey("configmap.yaml");
    }

    [Fact]
    public async Task GenerateAsync_BackendDeploymentYaml_ContainsExpectedFields()
    {
        var (step, action) = CreateBackendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("name: squid-api-deployment");
        yaml.ShouldContain("namespace: squid-backend-namespace");
        yaml.ShouldContain("replicas: 2");
        yaml.ShouldContain("image: repo/squid-api:1.0");
        yaml.ShouldContain("containerPort: 8080");
        yaml.ShouldContain("envFrom:");
        yaml.ShouldContain("squid-api-config-variables");
    }

    [Fact]
    public async Task GenerateAsync_BackendServiceYaml_ContainsExpectedFields()
    {
        var (step, action) = CreateBackendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["service.yaml"]);

        yaml.ShouldContain("kind: Service");
        yaml.ShouldContain("name: squid-api-service");
        yaml.ShouldContain("type: ClusterIP");
        yaml.ShouldContain("port: 8080");
        yaml.ShouldContain("targetPort: 8080");
    }

    [Fact]
    public async Task GenerateAsync_BackendConfigMapYaml_ContainsExpectedFields()
    {
        var (step, action) = CreateBackendScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["configmap.yaml"]);

        yaml.ShouldContain("kind: ConfigMap");
        yaml.ShouldContain("name: squid-api-config-variables");
        yaml.ShouldContain("ConnectionStrings__DefaultConnection");
        yaml.ShouldContain("Host=example;Database=example;User Id=example;Password=example;");
    }

    // === Edge cases ===

    [Fact]
    public async Task GenerateAsync_WrongActionType_ReturnsEmpty()
    {
        var action = new DeploymentActionDto { ActionType = "Squid.Unknown" };
        var step = new DeploymentStepDto { Id = 1, Name = "test" };

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GenerateAsync_NoContainers_NoDeploymentYaml()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = "Squid.Action.KubernetesContainers.DeploymentName",
            PropertyValue = "test-deploy"
        });

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("deployment.yaml");
    }

    [Fact]
    public async Task GenerateAsync_NoServiceName_NoServiceYaml()
    {
        var (step, action) = CreateMinimalDeploymentScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldContainKey("deployment.yaml");
        result.ShouldNotContainKey("service.yaml");
    }

    [Fact]
    public async Task GenerateAsync_NoConfigMapName_NoConfigMapYaml()
    {
        var (step, action) = CreateMinimalDeploymentScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("configmap.yaml");
    }

    // === Deployment defaults and options ===

    [Fact]
    public async Task GenerateAsync_NoNamespace_DefaultsToDefault()
    {
        var (step, action) = CreateMinimalDeploymentScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("namespace: default");
    }

    [Fact]
    public async Task GenerateAsync_FallbackNamespace_UsesKubernetesNamespace()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.Kubernetes.Namespace", "fallback-ns");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("namespace: fallback-ns");
    }

    [Fact]
    public async Task GenerateAsync_NoDeploymentName_FallsBackToActionName()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto
        {
            ActionType = "Squid.KubernetesDeployContainers",
            Name = "my-action-name"
        };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers",
            """[{ "Name": "app", "Image": "nginx", "Ports": [{ "key": "http", "value": "80", "option": "TCP" }] }]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("name: my-action-name");
    }

    [Fact]
    public async Task GenerateAsync_NoDeploymentStyle_DefaultsToRollingUpdate()
    {
        var (step, action) = CreateMinimalDeploymentScenario();

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("type: RollingUpdate");
    }

    [Fact]
    public async Task GenerateAsync_InvalidReplicas_DefaultsToOne()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.Replicas", "abc");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("replicas: 1");
    }

    // === Container defaults ===

    [Fact]
    public async Task GenerateAsync_ContainerNoImage_DefaultsToNginxLatest()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers",
            """[{ "Name": "app", "Ports": [{ "key": "http", "value": "80", "option": "TCP" }] }]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("image: nginx:latest");
    }

    [Fact]
    public async Task GenerateAsync_ContainerPackageIdFallback_UsesPackageId()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers",
            """[{ "Name": "app", "PackageId": "myregistry/myapp", "Ports": [{ "key": "http", "value": "80", "option": "TCP" }] }]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("image: myregistry/myapp");
    }

    [Fact]
    public async Task GenerateAsync_ContainerEmptyName_DefaultsToContainer()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers",
            """[{ "Name": "", "Image": "nginx", "Ports": [{ "key": "http", "value": "80", "option": "TCP" }] }]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("- name: container");
    }

    // === Probes ===

    [Fact]
    public async Task GenerateAsync_LivenessProbe_HttpGet_GeneratesYaml()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
[{
  "Name": "app", "Image": "nginx",
  "Ports": [{ "key": "http", "value": "80", "option": "TCP" }],
  "LivenessProbe": {
    "httpGet": { "path": "/health", "port": "80", "scheme": "HTTP" },
    "initialDelaySeconds": "10",
    "periodSeconds": "30"
  }
}]
""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("livenessProbe:");
        yaml.ShouldContain("httpGet:");
        yaml.ShouldContain("path: /health");
        yaml.ShouldContain("port: 80");
        yaml.ShouldContain("initialDelaySeconds: 10");
        yaml.ShouldContain("periodSeconds: 30");
    }

    [Fact]
    public async Task GenerateAsync_ReadinessProbe_Exec_GeneratesYaml()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
[{
  "Name": "app", "Image": "nginx",
  "Ports": [{ "key": "http", "value": "80", "option": "TCP" }],
  "ReadinessProbe": {
    "exec": { "command": ["cat", "/tmp/healthy"] },
    "failureThreshold": "3"
  }
}]
""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("readinessProbe:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("command:");
        yaml.ShouldContain("- cat");
        yaml.ShouldContain("failureThreshold: 3");
    }

    [Fact]
    public async Task GenerateAsync_StartupProbe_TcpSocket_GeneratesYaml()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
[{
  "Name": "app", "Image": "nginx",
  "Ports": [{ "key": "http", "value": "80", "option": "TCP" }],
  "StartupProbe": {
    "tcpSocket": { "port": "8080" },
    "periodSeconds": "5"
  }
}]
""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("startupProbe:");
        yaml.ShouldContain("tcpSocket:");
        yaml.ShouldContain("port: 8080");
    }

    // === Security Context ===

    [Fact]
    public async Task GenerateAsync_SecurityContext_Capabilities_GeneratesYaml()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
[{
  "Name": "app", "Image": "nginx",
  "Ports": [{ "key": "http", "value": "80", "option": "TCP" }],
  "SecurityContext": {
    "runAsNonRoot": "true",
    "readOnlyRootFilesystem": "true",
    "capabilities": {
      "drop": ["ALL"],
      "add": ["NET_BIND_SERVICE"]
    }
  }
}]
""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("securityContext:");
        yaml.ShouldContain("runAsNonRoot: true");
        yaml.ShouldContain("readOnlyRootFilesystem: true");
        yaml.ShouldContain("capabilities:");
        yaml.ShouldContain("drop:");
        yaml.ShouldContain("- ALL");
        yaml.ShouldContain("add:");
        yaml.ShouldContain("- NET_BIND_SERVICE");
    }

    // === Lifecycle ===

    [Fact]
    public async Task GenerateAsync_Lifecycle_PreStop_GeneratesYaml()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
[{
  "Name": "app", "Image": "nginx",
  "Ports": [{ "key": "http", "value": "80", "option": "TCP" }],
  "Lifecycle": {
    "PreStop": {
      "exec": { "command": ["/bin/sh", "-c", "sleep 10"] }
    }
  }
}]
""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("lifecycle:");
        yaml.ShouldContain("preStop:");
        yaml.ShouldContain("exec:");
        yaml.ShouldContain("- /bin/sh");
    }

    // === Tolerations ===

    [Fact]
    public async Task GenerateAsync_Tolerations_GeneratesYaml()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.Tolerations",
            """[{"key": "dedicated", "operator": "Equal", "value": "web", "effect": "NoSchedule"}]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("tolerations:");
    }

    // === Annotations and Labels ===

    [Fact]
    public async Task GenerateAsync_DeploymentAnnotations_GeneratesYaml()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentAnnotations",
            """[{"Key": "prometheus.io/scrape", "Value": "true"}]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("annotations:");
        yaml.ShouldContain("prometheus.io/scrape: true");
    }

    [Fact]
    public async Task GenerateAsync_DeploymentLabels_UsedAsSelector()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentLabels",
            """[{"Key": "tier", "Value": "frontend"}]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("labels:");
        yaml.ShouldContain("tier: frontend");
        yaml.ShouldContain("matchLabels:");
    }

    // === ConfigMap edge cases ===

    [Fact]
    public async Task GenerateAsync_ConfigMapMultilineValue_UsesBlockScalar()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapName", "test-config");
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["config.yaml"] = "key1: value1\nkey2: value2"
            }));

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["configmap.yaml"]);

        yaml.ShouldContain("config.yaml: |");
    }

    [Fact]
    public async Task GenerateAsync_ConfigMapEmptyValue_RendersEmptyQuoted()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapName", "test-config");
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["EMPTY_VAR"] = ""
            }));

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["configmap.yaml"]);

        yaml.ShouldContain("EMPTY_VAR: \"\"");
    }

    // === Service with NodePort ===

    [Fact]
    public async Task GenerateAsync_ServiceWithNodePort_ContainsNodePort()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceName", "nodeport-svc");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceType", "NodePort");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"80","targetPort":"80","nodePort":"30080","protocol":"TCP"}]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["service.yaml"]);

        yaml.ShouldContain("type: NodePort");
        yaml.ShouldContain("nodePort: 30080");
    }

    // === Malformed JSON resilience ===

    [Fact]
    public async Task GenerateAsync_MalformedContainersJson_ReturnsNoDeployment()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", "not valid json {{{");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("deployment.yaml");
    }

    [Fact]
    public async Task GenerateAsync_MalformedServicePortsJson_NoServiceYaml()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceName", "test-svc");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServicePorts", "broken json");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);

        result.ShouldNotContainKey("service.yaml");
    }

    // === ImagePullSecrets ===

    [Fact]
    public async Task GenerateAsync_ImagePullSecrets_GeneratesYaml()
    {
        var (step, action) = CreateMinimalDeploymentScenario();
        AddProperty(action, "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets",
            """[{"name": "my-registry-secret"}]""");

        var result = await _generator.GenerateAsync(step, action, CancellationToken.None);
        var yaml = Encoding.UTF8.GetString(result["deployment.yaml"]);

        yaml.ShouldContain("imagePullSecrets:");
        yaml.ShouldContain("- name: my-registry-secret");
    }

    // === Helpers ===

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateFrontendScenario()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "Deploy frontend", StepType = "Deploy" };
        var action = new DeploymentActionDto
        {
            Id = 1,
            StepId = step.Id,
            Name = "Deploy postboyweb",
            ActionType = "Squid.KubernetesDeployContainers"
        };

        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentName", "postboyweb-deployment");
        AddProperty(action, "Squid.Action.KubernetesContainers.Namespace", "postboyweb-namespace");
        AddProperty(action, "Squid.Action.KubernetesContainers.Replicas", "1");
        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceName", "postboyweb-service");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceType", "ClusterIP");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"8000","targetPort":"80","nodePort":"","protocol":"TCP"}]""");
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
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
""");
        AddProperty(action, "Squid.Action.KubernetesContainers.CombinedVolumes", """
[
  {
    "Name": "postboyweb-config-volume",
    "Type": "ConfigMap",
    "ReferenceName": "postboyweb-config"
  }
]
""");
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapName", "postboyweb-config");
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["appsettings.json"] = "{ \"ApiBaseUrl\": \"https://api.example.com\" }"
            }));

        return (step, action);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateBackendScenario()
    {
        var step = new DeploymentStepDto { Id = 2, Name = "Deploy backend", StepType = "Deploy" };
        var action = new DeploymentActionDto
        {
            Id = 2,
            StepId = step.Id,
            Name = "Deploy squid-api",
            ActionType = "Squid.KubernetesDeployContainers"
        };

        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentName", "squid-api-deployment");
        AddProperty(action, "Squid.Action.KubernetesContainers.Namespace", "squid-backend-namespace");
        AddProperty(action, "Squid.Action.KubernetesContainers.Replicas", "2");
        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentStyle", "RollingUpdate");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceName", "squid-api-service");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServiceType", "ClusterIP");
        AddProperty(action, "Squid.Action.KubernetesContainers.ServicePorts",
            """[{"name":"http","port":"8080","targetPort":"8080","nodePort":"","protocol":"TCP"}]""");
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
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
""");
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapName", "squid-api-config-variables");
        AddProperty(action, "Squid.Action.KubernetesContainers.ConfigMapValues",
            JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["ConnectionStrings__DefaultConnection"] = "Host=example;Database=example;User Id=example;Password=example;",
                ["Serilog__MinimumLevel__Default"] = "Information"
            }));

        return (step, action);
    }

    private static (DeploymentStepDto step, DeploymentActionDto action) CreateMinimalDeploymentScenario()
    {
        var step = new DeploymentStepDto { Id = 1, Name = "test" };
        var action = new DeploymentActionDto { ActionType = "Squid.KubernetesDeployContainers" };

        AddProperty(action, "Squid.Action.KubernetesContainers.DeploymentName", "minimal-deploy");
        AddProperty(action, "Squid.Action.KubernetesContainers.Containers", """
[{ "Name": "app", "Image": "nginx:latest", "Ports": [{ "key": "http", "value": "80", "option": "TCP" }] }]
""");

        return (step, action);
    }

    private static void AddProperty(DeploymentActionDto action, string name, string value)
    {
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = name,
            PropertyValue = value
        });
    }
}
