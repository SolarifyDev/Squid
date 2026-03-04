using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class FeedSecretGenerationTests
{
    // === HasCreateFeedSecrets ===

    [Theory]
    [InlineData("True", true)]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("false", false)]
    public void HasCreateFeedSecrets_ReturnsExpected(string createFeedSecretsValue, bool expected)
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "myapp",
                ["CreateFeedSecrets"] = createFeedSecretsValue
            }
        });

        var action = CreateActionWithContainers(containerJson);

        KubernetesYamlActionHandler.HasCreateFeedSecrets(action).ShouldBe(expected);
    }

    [Fact]
    public void HasCreateFeedSecrets_MissingProperty_ReturnsFalse()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "myapp" }
        });

        var action = CreateActionWithContainers(containerJson);

        KubernetesYamlActionHandler.HasCreateFeedSecrets(action).ShouldBeFalse();
    }

    [Fact]
    public void HasCreateFeedSecrets_NoContainersProperty_ReturnsFalse()
    {
        var action = new DeploymentActionDto { Properties = new List<DeploymentActionPropertyDto>() };

        KubernetesYamlActionHandler.HasCreateFeedSecrets(action).ShouldBeFalse();
    }

    [Fact]
    public void HasCreateFeedSecrets_RealProductionContainerJson_ReturnsTrue()
    {
        // Real production container JSON — CreateFeedSecrets is the last field
        // among ~20+ fields including Ports, Resources, Probes, SecurityContext, Lifecycle, etc.
        const string realContainerJson = """
            [
             {
              "Name": "smarttalk-webapi",
              "FeedId": "Feeds-1061",
              "Ports": [
               {
                "key": "http",
                "value": "8080",
                "option": "TCP"
               }
              ],
              "EnvironmentVariables": [],
              "SecretEnvironmentVariables": [],
              "ConfigMapEnvironmentVariables": [],
              "FieldRefEnvironmentVariables": [],
              "ConfigMapEnvFromSource": [
               {
                "key": "configurations-smarttalk-#{Squid.Deployment.Id | ToLower}",
                "value": "",
                "option": ""
               }
              ],
              "SecretEnvFromSource": [],
              "VolumeMounts": [
               {
                "key": "config",
                "value": "/root/.ssh",
                "option": ""
               }
              ],
              "Resources": {
               "requests": {
                "memory": "#{MemoryResourceRequest}",
                "cpu": "#{CpuResourceRequest}",
                "ephemeralStorage": ""
               },
               "limits": {
                "memory": "#{MemoryResourceLimit}",
                "cpu": "#{CpuResourceLimit}",
                "ephemeralStorage": "",
                "nvidiaGpu": "",
                "amdGpu": ""
               }
              },
              "LivenessProbe": {
               "failureThreshold": "",
               "initialDelaySeconds": "",
               "periodSeconds": "",
               "successThreshold": "",
               "timeoutSeconds": "",
               "type": null,
               "exec": { "command": [] },
               "httpGet": { "host": "", "path": "", "port": "", "scheme": "", "httpHeaders": [] },
               "tcpSocket": { "host": "", "port": "" }
              },
              "ReadinessProbe": {
               "failureThreshold": "",
               "initialDelaySeconds": "",
               "periodSeconds": "",
               "successThreshold": "",
               "timeoutSeconds": "",
               "type": null,
               "exec": { "command": [] },
               "httpGet": { "host": "", "path": "", "port": "", "scheme": "", "httpHeaders": [] },
               "tcpSocket": { "host": "", "port": "" }
              },
              "StartupProbe": {
               "failureThreshold": "",
               "initialDelaySeconds": "",
               "periodSeconds": "",
               "successThreshold": "",
               "timeoutSeconds": "",
               "type": null,
               "exec": { "command": [] },
               "httpGet": { "host": "", "path": "", "port": "", "scheme": "", "httpHeaders": [] },
               "tcpSocket": { "host": "", "port": "" }
              },
              "Command": [],
              "Args": [],
              "InitContainer": "False",
              "SecurityContext": {
               "allowPrivilegeEscalation": "",
               "privileged": "",
               "readOnlyRootFilesystem": "",
               "runAsGroup": "",
               "runAsNonRoot": "",
               "runAsUser": "",
               "capabilities": { "add": [], "drop": [] },
               "seLinuxOptions": { "level": "", "role": "", "type": "", "user": "" }
              },
              "Lifecycle": {},
              "CreateFeedSecrets": "True"
             }
            ]
            """;

        var action = CreateActionWithContainers(realContainerJson);

        KubernetesYamlActionHandler.HasCreateFeedSecrets(action).ShouldBeTrue();
    }

    // === InjectImagePullSecret ===

    [Fact]
    public void InjectImagePullSecret_NoExisting_CreatesProperty()
    {
        var action = new DeploymentActionDto
        {
            Id = 1,
            Properties = new List<DeploymentActionPropertyDto>()
        };

        KubernetesYamlActionHandler.InjectImagePullSecret(action, "my-registry-secret");

        var prop = action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets");
        var secrets = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(prop.PropertyValue);

        secrets.Count.ShouldBe(1);
        secrets[0]["name"].ShouldBe("my-registry-secret");
    }

    [Fact]
    public void InjectImagePullSecret_HasExisting_Appends()
    {
        var existingSecrets = JsonSerializer.Serialize(new[] { new { name = "existing-secret" } });

        var action = new DeploymentActionDto
        {
            Id = 1,
            Properties = new List<DeploymentActionPropertyDto>
            {
                new()
                {
                    PropertyName = "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets",
                    PropertyValue = existingSecrets
                }
            }
        };

        KubernetesYamlActionHandler.InjectImagePullSecret(action, "new-registry-secret");

        var prop = action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets");
        var secrets = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(prop.PropertyValue);

        secrets.Count.ShouldBe(2);
        secrets[0]["name"].ShouldBe("existing-secret");
        secrets[1]["name"].ShouldBe("new-registry-secret");
    }

    [Fact]
    public void InjectImagePullSecret_Duplicate_NoDuplicate()
    {
        var existingSecrets = JsonSerializer.Serialize(new[] { new { name = "my-registry-secret" } });

        var action = new DeploymentActionDto
        {
            Id = 1,
            Properties = new List<DeploymentActionPropertyDto>
            {
                new()
                {
                    PropertyName = "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets",
                    PropertyValue = existingSecrets
                }
            }
        };

        KubernetesYamlActionHandler.InjectImagePullSecret(action, "my-registry-secret");

        var prop = action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets");
        var secrets = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(prop.PropertyValue);

        secrets.Count.ShouldBe(1);
    }

    // === BuildDockerConfigJson ===

    [Fact]
    public void BuildDockerConfigJson_ValidCredentials_ProducesCorrectJson()
    {
        var result = KubernetesYamlActionHandler.BuildDockerConfigJson(
            "registry.example.com", "user", "pass123");

        using var doc = JsonDocument.Parse(result);
        var auths = doc.RootElement.GetProperty("auths");
        var registry = auths.GetProperty("registry.example.com");

        registry.GetProperty("username").GetString().ShouldBe("user");
        registry.GetProperty("password").GetString().ShouldBe("pass123");

        var expectedAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes("user:pass123"));
        registry.GetProperty("auth").GetString().ShouldBe(expectedAuth);
    }

    // === GenerateSecretYaml ===

    [Fact]
    public void GenerateSecretYaml_ProducesValidYaml()
    {
        var dockerConfigJson = "{\"auths\":{}}";

        var result = KubernetesYamlActionHandler.GenerateSecretYaml(
            "my-secret", "production", dockerConfigJson);

        result.ShouldContain("kind: Secret");
        result.ShouldContain("name: my-secret");
        result.ShouldContain("namespace: production");
        result.ShouldContain("type: kubernetes.io/dockerconfigjson");

        var expectedData = Convert.ToBase64String(Encoding.UTF8.GetBytes(dockerConfigJson));
        result.ShouldContain($".dockerconfigjson: {expectedData}");
    }

    // === BuildFeedSecretName ===

    [Theory]
    [InlineData("my-feed", "MyFeed", 10, "my-feed-registry-secret")]
    [InlineData(null, "MyFeed", 10, "myfeed-registry-secret")]
    [InlineData(null, null, 42, "feed-42-registry-secret")]
    public void BuildFeedSecretName_ReturnsExpected(string slug, string name, int id, string expected)
    {
        var feed = new ExternalFeed { Id = id, Slug = slug, Name = name };

        KubernetesYamlActionHandler.BuildFeedSecretName(feed).ShouldBe(expected);
    }

    // === GetNamespaceFromAction ===

    [Theory]
    [InlineData("Squid.Action.KubernetesContainers.Namespace", "staging", "staging")]
    [InlineData("Squid.Action.Kubernetes.Namespace", "production", "production")]
    public void GetNamespaceFromAction_ReturnsExpected(string propName, string propValue, string expected)
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>
            {
                new() { PropertyName = propName, PropertyValue = propValue }
            }
        };

        KubernetesYamlActionHandler.GetNamespaceFromAction(action).ShouldBe(expected);
    }

    [Fact]
    public void GetNamespaceFromAction_NoNamespaceProperty_ReturnsDefault()
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>()
        };

        KubernetesYamlActionHandler.GetNamespaceFromAction(action).ShouldBe("default");
    }

    [Fact]
    public void GetNamespaceFromAction_ContainersNamespaceTakesPriority()
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>
            {
                new()
                {
                    PropertyName = "Squid.Action.KubernetesContainers.Namespace",
                    PropertyValue = "containers-ns"
                },
                new()
                {
                    PropertyName = "Squid.Action.Kubernetes.Namespace",
                    PropertyValue = "k8s-ns"
                }
            }
        };

        KubernetesYamlActionHandler.GetNamespaceFromAction(action).ShouldBe("containers-ns");
    }

    // === Integration: PrepareAsync ===

    [Fact]
    public async Task PrepareAsync_CreateFeedSecretsTrue_GeneratesFeedSecretsYaml()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "myapp",
                ["CreateFeedSecrets"] = "True"
            }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "myapp");
        action.Properties.Add(new DeploymentActionPropertyDto
        {
            PropertyName = "Squid.Action.KubernetesContainers.Namespace",
            PropertyValue = "staging"
        });

        var ctx = CreateContext(action, packageVersion: "1.0.0");

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 10,
                Name = "MyRegistry",
                FeedUri = "https://registry.example.com/v2",
                Username = "user",
                Password = "secret"
            });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldContainKey("feedsecrets.yaml");

        var secretYaml = Encoding.UTF8.GetString(result.Files["feedsecrets.yaml"]);
        secretYaml.ShouldContain("kind: Secret");
        secretYaml.ShouldContain("name: myregistry-registry-secret");
        secretYaml.ShouldContain("namespace: staging");

        var pullSecretsProp = action.Properties
            .FirstOrDefault(p => p.PropertyName == "Squid.Action.KubernetesContainers.PodSecurityImagePullSecrets");
        pullSecretsProp.ShouldNotBeNull();
        pullSecretsProp.PropertyValue.ShouldContain("myregistry-registry-secret");
    }

    [Fact]
    public async Task PrepareAsync_CreateFeedSecretsFalse_NoFeedSecretsYaml()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "myapp",
                ["CreateFeedSecrets"] = "False"
            }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "myapp");
        var ctx = CreateContext(action, packageVersion: "1.0.0");

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("feedsecrets.yaml");
    }

    [Fact]
    public async Task PrepareAsync_FeedNoCredentials_NoFeedSecretsYaml()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "myapp",
                ["CreateFeedSecrets"] = "True"
            }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "myapp");
        var ctx = CreateContext(action, packageVersion: "1.0.0");

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 10,
                FeedUri = "https://registry.example.com/v2",
                Username = "user",
                Password = null
            });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("feedsecrets.yaml");
    }

    [Fact]
    public async Task PrepareAsync_FeedNotFound_NoFeedSecretsYaml()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "myapp",
                ["CreateFeedSecrets"] = "True"
            }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "myapp");
        var ctx = CreateContext(action, packageVersion: "1.0.0");

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExternalFeed)null);

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("feedsecrets.yaml");
    }

    [Fact]
    public async Task PrepareAsync_NoFeedId_NoFeedSecretsYaml()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object>
            {
                ["Name"] = "web",
                ["Image"] = "myapp",
                ["CreateFeedSecrets"] = "True"
            }
        });

        var action = CreateActionWithContainers(containerJson, feedId: null, packageId: "myapp");
        var ctx = CreateContext(action, packageVersion: "1.0.0");

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        var result = await handler.PrepareAsync(ctx, CancellationToken.None);

        result.Files.ShouldNotContainKey("feedsecrets.yaml");
    }

    // === Helpers ===

    private static DeploymentActionDto CreateActionWithContainers(
        string containerJson, int? feedId = null, string packageId = null)
    {
        return new DeploymentActionDto
        {
            Name = "Deploy",
            ActionType = "Squid.KubernetesDeployContainers",
            FeedId = feedId,
            PackageId = packageId,
            Properties = new List<DeploymentActionPropertyDto>
            {
                new()
                {
                    PropertyName = "Squid.Action.KubernetesContainers.Containers",
                    PropertyValue = containerJson
                }
            }
        };
    }

    private static ActionExecutionContext CreateContext(DeploymentActionDto action, string packageVersion)
    {
        var variables = new List<VariableDto>();

        if (packageVersion != null)
        {
            variables.Add(new VariableDto
            {
                Name = SpecialVariables.Action.PackageVersion,
                Value = packageVersion
            });
        }

        return new ActionExecutionContext
        {
            Action = action,
            Step = new DeploymentStepDto(),
            Variables = variables,
            ReleaseVersion = "1.0.0"
        };
    }

    private static Mock<IActionYamlGenerator> CreateMockGenerator(bool canHandle)
    {
        var mock = new Mock<IActionYamlGenerator>();
        mock.Setup(g => g.CanHandle(It.IsAny<DeploymentActionDto>())).Returns(canHandle);
        mock.Setup(g => g.GenerateAsync(
                It.IsAny<DeploymentStepDto>(),
                It.IsAny<DeploymentActionDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, byte[]>());
        return mock;
    }
}
