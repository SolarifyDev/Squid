using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Process;
using Squid.Core.Services.Deployments.Process.Action;
using Squid.Core.Services.Deployments.Process.Step;
using Squid.Core.Services.Deployments.Project;
using Squid.Core.Services.Deployments.Release;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.Deployments.Process;

public class DeploymentPackageReferenceServiceTests
{
    private readonly Mock<IProjectDataProvider> _projectMock = new();
    private readonly Mock<IDeploymentProcessDataProvider> _processMock = new();
    private readonly Mock<IDeploymentStepDataProvider> _stepMock = new();
    private readonly Mock<IDeploymentActionDataProvider> _actionMock = new();
    private readonly Mock<IDeploymentActionPropertyDataProvider> _propertyMock = new();
    private readonly Mock<IActionChannelDataProvider> _actionChannelMock = new();
    private readonly Mock<IExternalFeedDataProvider> _feedMock = new();
    private readonly Mock<IReleaseDataProvider> _releaseMock = new();
    private readonly Mock<IReleaseSelectedPackageDataProvider> _selectedPackageMock = new();

    private DeploymentPackageReferenceService CreateService() => new(
        _projectMock.Object, _processMock.Object, _stepMock.Object,
        _actionMock.Object, _propertyMock.Object, _actionChannelMock.Object,
        _feedMock.Object, _releaseMock.Object, _selectedPackageMock.Object);

    private void SetupBasicProjectPipeline(List<DeploymentAction> actions, List<DeploymentActionProperty> properties)
    {
        var project = new Squid.Core.Persistence.Entities.Deployments.Project { Id = 1, DeploymentProcessId = 10 };
        var process = new DeploymentProcess { Id = 10 };
        var steps = new List<DeploymentStep> { new() { Id = 100 } };

        _projectMock.Setup(p => p.GetProjectByIdAsync(1, It.IsAny<CancellationToken>())).ReturnsAsync(project);
        _processMock.Setup(p => p.GetDeploymentProcessByIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(process);
        _stepMock.Setup(s => s.GetDeploymentStepsByProcessIdAsync(10, It.IsAny<CancellationToken>())).ReturnsAsync(steps);
        _actionMock.Setup(a => a.GetDeploymentActionsByStepIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(actions);
        _propertyMock.Setup(p => p.GetDeploymentActionPropertiesByActionIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(properties);
        _actionChannelMock.Setup(c => c.GetActionChannelsByActionIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ActionChannel>());
        _feedMock.Setup(f => f.GetExternalFeedsByIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ExternalFeed>());
        _releaseMock.Setup(r => r.GetReleasesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((0, new List<Squid.Core.Persistence.Entities.Deployments.Release>()));
    }

    [Fact]
    public async Task GetPackageReferences_ActionLevelFeedId_Detected()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "DeployYaml", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "k8s-manifests" }
        };
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1);

        refs.ShouldContain(r => r.ActionName == "DeployYaml" && r.PackageId == "k8s-manifests" && r.FeedId == 5);
    }

    [Fact]
    public async Task GetPackageReferences_BothContainerAndActionLevel_BothDetected()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "DeployContainers", StepId = 100 },
            new() { Id = 2, Name = "DeployYaml", StepId = 100 }
        };
        var containerJson = "[{\"PackageId\":\"nginx\",\"FeedId\":3,\"Name\":\"web\"}]";
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = "Squid.Action.KubernetesContainers.Containers", PropertyValue = containerJson },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "k8s-manifests" }
        };
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1);

        refs.Count.ShouldBe(2);
        refs.ShouldContain(r => r.PackageId == "nginx" && r.PackageReferenceName == "web");
        refs.ShouldContain(r => r.PackageId == "k8s-manifests" && r.PackageReferenceName == string.Empty);
    }

    [Fact]
    public async Task GetPackageReferences_NoPackageProperties_Empty()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "DeployYaml", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>();
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1);

        refs.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPackageReferences_InvalidFeedId_Skipped()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "DeployYaml", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "not-a-number" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "k8s-manifests" }
        };
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1);

        refs.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPackageReferences_DisabledAction_Excluded()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "EnabledAction", StepId = 100 },
            new() { Id = 2, Name = "DisabledAction", StepId = 100, IsDisabled = true }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-enabled" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-disabled" }
        };
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1);

        refs.Count.ShouldBe(1);
        refs.ShouldContain(r => r.PackageId == "pkg-enabled");
        refs.ShouldNotContain(r => r.PackageId == "pkg-disabled");
    }

    [Fact]
    public async Task GetPackageReferences_ChannelMismatch_Excluded()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "ChannelRestricted", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-a" }
        };
        SetupBasicProjectPipeline(actions, properties);
        _actionChannelMock.Setup(c => c.GetActionChannelsByActionIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionChannel> { new() { ActionId = 1, ChannelId = 1 }, new() { ActionId = 1, ChannelId = 2 } });

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1, channelId: 3);

        refs.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetPackageReferences_ChannelMatch_Included()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "ChannelRestricted", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-a" }
        };
        SetupBasicProjectPipeline(actions, properties);
        _actionChannelMock.Setup(c => c.GetActionChannelsByActionIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionChannel> { new() { ActionId = 1, ChannelId = 1 }, new() { ActionId = 1, ChannelId = 2 } });

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1, channelId: 1);

        refs.Count.ShouldBe(1);
        refs.ShouldContain(r => r.PackageId == "pkg-a");
    }

    [Fact]
    public async Task GetPackageReferences_NoChannelRestriction_IncludedForAnyChannel()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "UnrestrictedAction", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-a" }
        };
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1, channelId: 99);

        refs.Count.ShouldBe(1);
        refs.ShouldContain(r => r.PackageId == "pkg-a");
    }

    [Fact]
    public async Task GetPackageReferences_NullChannelId_OnlyFiltersDisabled()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "EnabledAction", StepId = 100 },
            new() { Id = 2, Name = "DisabledAction", StepId = 100, IsDisabled = true }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-enabled" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-disabled" }
        };
        SetupBasicProjectPipeline(actions, properties);

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1, channelId: null);

        refs.Count.ShouldBe(1);
        refs.ShouldContain(r => r.PackageId == "pkg-enabled");
        _actionChannelMock.Verify(c => c.GetActionChannelsByActionIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPackageReferences_DisabledAndChannelMismatch_BothExcluded()
    {
        var actions = new List<DeploymentAction>
        {
            new() { Id = 1, Name = "EnabledMatchingChannel", StepId = 100 },
            new() { Id = 2, Name = "DisabledAction", StepId = 100, IsDisabled = true },
            new() { Id = 3, Name = "WrongChannel", StepId = 100 }
        };
        var properties = new List<DeploymentActionProperty>
        {
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 1, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-keep" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 2, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-disabled" },
            new() { ActionId = 3, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "5" },
            new() { ActionId = 3, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = "pkg-wrong-channel" }
        };
        SetupBasicProjectPipeline(actions, properties);
        _actionChannelMock.Setup(c => c.GetActionChannelsByActionIdsAsync(It.IsAny<List<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActionChannel>
            {
                new() { ActionId = 1, ChannelId = 1 },
                new() { ActionId = 3, ChannelId = 2 }
            });

        var service = CreateService();
        var refs = await service.GetPackageReferencesAsync(1, channelId: 1);

        refs.Count.ShouldBe(1);
        refs.ShouldContain(r => r.PackageId == "pkg-keep");
    }
}
