using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Kubernetes;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Execution;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.UnitTests.Services.Deployments.Kubernetes;

public class ContainerImageResolutionTests
{
    [Fact]
    public async Task ResolveContainerImages_WithFeedAndVersion_UpdatesContainerJson()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "smarttalk/webapi", ["Ports"] = new[] { 80 } }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "smarttalk/webapi");
        var ctx = CreateContext(action, packageVersion: "1.2.3");

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
    public async Task ResolveContainerImages_NoPackageVersion_NoChange()
    {
        var originalJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "original/image" }
        });

        var action = CreateActionWithContainers(originalJson, feedId: 10, packageId: "original/image");
        var ctx = CreateContext(action, packageVersion: null);

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var prop = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");

        prop.PropertyValue.ShouldBe(originalJson);
        feedProvider.Verify(f => f.GetFeedByIdAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveContainerImages_NoFeedId_NoChange()
    {
        var originalJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "original/image" }
        });

        var action = CreateActionWithContainers(originalJson, feedId: null, packageId: "original/image");
        var ctx = CreateContext(action, packageVersion: "1.0.0");

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
        var feed = new ExternalFeed { RegistryPath = registryPath, FeedUri = feedUri };

        var result = KubernetesEndpointVariableContributor.ResolveFeedUri(feed);

        result.ShouldBe(expected);
    }

    [Fact]
    public void UpdateContainerImages_MultipleContainers_AllUpdated()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "old/web" },
            new Dictionary<string, object> { ["Name"] = "sidecar", ["Image"] = "old/sidecar" }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "myapp");

        KubernetesYamlActionHandler.UpdateContainerImages(action, "registry.io/myapp:2.0.0");

        var updatedProp = action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers.Count.ShouldBe(2);
        containers[0]["Image"].GetString().ShouldBe("registry.io/myapp:2.0.0");
        containers[1]["Image"].GetString().ShouldBe("registry.io/myapp:2.0.0");
    }

    [Fact]
    public void UpdateContainerImages_NoContainersProperty_NoError()
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>()
        };

        KubernetesYamlActionHandler.UpdateContainerImages(action, "registry.io/myapp:1.0.0");

        // Should not throw
        action.Properties.ShouldBeEmpty();
    }

    [Fact]
    public void UpdateContainerImages_InvalidJson_NoError()
    {
        var action = new DeploymentActionDto
        {
            Properties = new List<DeploymentActionPropertyDto>
            {
                new()
                {
                    PropertyName = "Squid.Action.KubernetesContainers.Containers",
                    PropertyValue = "not-valid-json"
                }
            }
        };

        KubernetesYamlActionHandler.UpdateContainerImages(action, "registry.io/myapp:1.0.0");

        // Should not throw, original value unchanged
        action.Properties[0].PropertyValue.ShouldBe("not-valid-json");
    }

    [Fact]
    public async Task ResolveContainerImages_FeedWithRegistryPath_UsesRegistryPath()
    {
        var containerJson = JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, object> { ["Name"] = "web", ["Image"] = "library/nginx" }
        });

        var action = CreateActionWithContainers(containerJson, feedId: 10, packageId: "library/nginx");
        var ctx = CreateContext(action, packageVersion: "1.25.0");

        var feedProvider = new Mock<IExternalFeedDataProvider>();
        feedProvider.Setup(f => f.GetFeedByIdAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExternalFeed
            {
                Id = 10,
                FeedUri = "https://index.docker.io/v2",
                RegistryPath = "docker.io"
            });

        var generator = CreateMockGenerator(canHandle: true);
        var handler = new KubernetesYamlActionHandler(new[] { generator.Object }, feedProvider.Object);

        await handler.PrepareAsync(ctx, CancellationToken.None);

        var updatedProp = ctx.Action.Properties
            .First(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers");
        var containers = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(updatedProp.PropertyValue);

        containers[0]["Image"].GetString().ShouldBe("docker.io/library/nginx:1.25.0");
    }

    private static DeploymentActionDto CreateActionWithContainers(string containerJson, int? feedId, string packageId)
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
