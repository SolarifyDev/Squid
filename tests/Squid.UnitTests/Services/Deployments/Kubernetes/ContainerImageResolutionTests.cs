using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;
using Squid.Core.Services.DeploymentExecution.Handlers;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class ContainerImageResolutionTests
{
    [Fact]
    public async Task ResolveContainerImages_WithFeedAndVersion_UpdatesContainerJson()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "smarttalk/webapi", ["PackageId"] = "smarttalk/webapi", ["FeedId"] = 10, ["Ports"] = new[] { 80 } }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.2.3" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://registry.example.com/v2" });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("registry.example.com/smarttalk/webapi:1.2.3");
    }

    [Fact]
    public async Task ResolveContainerImages_NoSelectedPackage_NoChange()
    {
        var originalJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "original/image", ["PackageId"] = "original/image", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(originalJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: Array.Empty<SelectedPackageDto>());

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var prop = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");

        prop.PropertyValue.ShouldBe(originalJson);
    }

    [Fact]
    public async Task ResolveContainerImages_NoFeedId_NoChange()
    {
        var originalJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "original/image", ["PackageId"] = "original/image" }
        });

        var action = CreateActionWithContainers(originalJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.0.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var prop = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");

        prop.PropertyValue.ShouldBe(originalJson);
        feedProvider.Verify(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("docker.io", null, "docker.io")]
    [InlineData(null, "https://registry.example.com/v2", "registry.example.com")]
    [InlineData(null, "https://registry.internal.com:5000/v2", "registry.internal.com:5000")]
    public void ResolveFeedUri_ReturnsExpectedUri(string registryPath, string feedUri, string expected)
    {
        var feed = new ExternalFeed { Properties = ExternalFeedProperties.Serialize(registryPath != null ? new Dictionary<string, string> { ["RegistryPath"] = registryPath } : null), FeedUri = feedUri };

        var result = KubernetesApiEndpointVariableContributor.ResolveFeedUri(feed);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task ResolveContainerImages_MultipleContainers_EachGetsOwnImage()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "old/web", ["PackageId"] = "library/nginx", ["FeedId"] = 10 },
            new Dictionary<string, object> { ["Name"] = "redis", ["Image"] = "old/redis", ["PackageId"] = "library/redis", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.25.0" },
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "redis", Version = "7.2.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers.Count.ShouldBe(2);
        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
        containers[1]["Image"].GetString().ShouldBe("docker.io/library/redis:7.2.0");
    }

    [Fact]
    public async Task ResolveContainerImages_ContainerWithoutPackage_KeepsStaticImage()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "library/nginx", ["PackageId"] = "library/nginx", ["FeedId"] = 10 },
            new Dictionary<string, object> { ["Name"] = "sidecar", ["Image"] = "static/sidecar:latest" }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.25.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers.Count.ShouldBe(2);
        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
        containers[1]["Image"].GetString().ShouldBe("static/sidecar:latest");
    }

    [Fact]
    public async Task ResolveContainerImages_MixedFeeds_EachResolvesFromOwnFeed()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "old/web", ["PackageId"] = "library/nginx", ["FeedId"] = 10 },
            new Dictionary<string, object> { ["Name"] = "api", ["Image"] = "old/api", ["PackageId"] = "mycompany/api", ["FeedId"] = 20 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.25.0" },
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "api", Version = "3.0.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });
        feedProvider.Setup(f => f.GetFeedByIdAsync(20, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 20, FeedUri = "https://registry.internal.com/v2" });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
        containers[1]["Image"].GetString().ShouldBe("registry.internal.com/mycompany/api:3.0.0");
    }

    [Fact]
    public async Task ResolveContainerImages_FeedWithRegistryPath_UsesRegistryPath()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "library/nginx", ["PackageId"] = "library/nginx", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.25.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 10,
                FeedUri = "https://index.docker.io/v2",
                Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" })
            });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
    }

    [Fact]
    public async Task ResolveContainerImages_ActionNameMismatch_FallsBackToPackageVersionVariable()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "old/web", ["PackageId"] = "library/nginx", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy API");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy Web", PackageReferenceName = "web", Version = "2.0.0" }
        });
        ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Action.PackageVersion, Value = "1.0.0" });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.0.0");
    }

    [Fact]
    public async Task ResolveContainerImages_ContainerNameMismatch_FallsBackToPackageVersionVariable()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web-v2", ["Image"] = "old/web", ["PackageId"] = "library/nginx", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "2.0.0" }
        });
        ctx.Variables.Add(new VariableDto { Name = SpecialVariables.Action.PackageVersion, Value = "1.0.0" });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.0.0");
    }

    [Fact]
    public async Task ResolveContainerImages_NameMatchIsCaseInsensitive()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "Web-Server", ["Image"] = "old/web", ["PackageId"] = "library/nginx", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web-server", Version = "1.25.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
    }

    [Fact]
    public async Task ResolveContainerImages_FeedIdAsString_ResolvedCorrectly()
    {
        var containerJson = """[{"Name":"web","Image":"old/web","PackageId":"library/nginx","FeedId":"10"}]""";

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Deploy", PackageReferenceName = "web", Version = "1.25.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed { Id = 10, FeedUri = "https://index.docker.io/v2", Properties = ExternalFeedProperties.Serialize(new Dictionary<string, string> { ["RegistryPath"] = "docker.io" }) });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
    }

    [Fact]
    public async Task ResolveContainerImages_NoMatchNoFallback_ImageNotUpdated()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "original/image:v1", ["PackageId"] = "library/nginx", ["FeedId"] = 10 }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");
        var ctx = CreateContext(action, selectedPackages: new[]
        {
            new SelectedPackageDto { ActionName = "Other Action", PackageReferenceName = "web", Version = "2.0.0" }
        });

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var prop = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(prop.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("original/image:v1");
    }

    [Fact]
    public void CollectFeedIdsRequiringSecrets_MultipleContainersMultipleFeeds_ReturnsUniqueFeedIds()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["PackageId"] = "library/nginx", ["FeedId"] = 10, ["CreateFeedSecrets"] = "True" },
            new Dictionary<string, object> { ["Name"] = "api", ["PackageId"] = "mycompany/api", ["FeedId"] = 20, ["CreateFeedSecrets"] = "True" },
            new Dictionary<string, object> { ["Name"] = "sidecar", ["PackageId"] = "mycompany/sidecar", ["FeedId"] = 10, ["CreateFeedSecrets"] = "True" }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");

        var feedIds = KubernetesYamlActionHandler.CollectFeedIdsRequiringSecrets(action);

        feedIds.Count.ShouldBe(2);
        feedIds.ShouldContain(10);
        feedIds.ShouldContain(20);
    }

    [Fact]
    public void CollectFeedIdsRequiringSecrets_NoFeedSecrets_ReturnsEmpty()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["PackageId"] = "library/nginx", ["FeedId"] = 10, ["CreateFeedSecrets"] = "False" }
        });

        var action = CreateActionWithContainers(containerJson, actionName: "Deploy");

        var feedIds = KubernetesYamlActionHandler.CollectFeedIdsRequiringSecrets(action);

        feedIds.ShouldBeEmpty();
    }

    [Fact]
    public void ResolvePackageVersion_MatchByActionAndContainer_ReturnsVersion()
    {
        var action = new DeploymentActionDto { Name = "Deploy API" };
        var ctx = new ActionExecutionContext
        {
            Action = action,
            Step = new DeploymentStepDto(),
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy API", PackageReferenceName = "web", Version = "3.0.1" },
                new() { ActionName = "Deploy API", PackageReferenceName = "worker", Version = "1.5.0" },
                new() { ActionName = "Deploy Web", PackageReferenceName = "web", Version = "2.1.0" }
            }
        };

        KubernetesYamlActionHandler.ResolvePackageVersion(ctx, "worker").ShouldBe("1.5.0");
        KubernetesYamlActionHandler.ResolvePackageVersion(ctx, "web").ShouldBe("3.0.1");
    }

    [Fact]
    public void ResolvePackageVersion_NoMatch_FallsBackToVariable()
    {
        var action = new DeploymentActionDto { Name = "Deploy API" };
        var ctx = new ActionExecutionContext
        {
            Action = action,
            Step = new DeploymentStepDto(),
            Variables = new List<VariableDto>
            {
                new() { Name = SpecialVariables.Action.PackageVersion, Value = "1.0.0" }
            },
            SelectedPackages = new List<SelectedPackageDto>
            {
                new() { ActionName = "Deploy Web", PackageReferenceName = "web", Version = "2.0.0" }
            }
        };

        KubernetesYamlActionHandler.ResolvePackageVersion(ctx, "web").ShouldBe("1.0.0");
    }

    [Fact]
    public void ResolvePackageVersion_EmptySelectedPackages_FallsBackToVariable()
    {
        var action = new DeploymentActionDto { Name = "Deploy" };
        var ctx = new ActionExecutionContext
        {
            Action = action,
            Step = new DeploymentStepDto(),
            Variables = new List<VariableDto>
            {
                new() { Name = SpecialVariables.Action.PackageVersion, Value = "1.0.0" }
            },
            SelectedPackages = new List<SelectedPackageDto>()
        };

        KubernetesYamlActionHandler.ResolvePackageVersion(ctx, "web").ShouldBe("1.0.0");
    }

    private static DeploymentActionDto CreateActionWithContainers(string containerJson, string actionName = "Deploy")
    {
        return new DeploymentActionDto
        {
            Name = actionName,
            ActionType = "Squid.KubernetesDeployContainers",
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

    private static ActionExecutionContext CreateContext(DeploymentActionDto action, SelectedPackageDto[] selectedPackages = null)
    {
        return new ActionExecutionContext
        {
            Action = action,
            Step = new DeploymentStepDto(),
            Variables = new List<VariableDto>(),
            ReleaseVersion = "1.0.0",
            SelectedPackages = selectedPackages?.ToList() ?? new()
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
