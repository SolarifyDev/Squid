using System.Linq;
using Squid.Core.Services.ProjectTriggers;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.UnitTests.Services.ProjectTriggers;

public class ProjectTriggerRegistryTests
{
    [Theory]
    [InlineData(ProjectTriggerTypes.GitRepository, ProjectTriggerActions.CreateRelease)]
    [InlineData(ProjectTriggerTypes.ExternalFeed, ProjectTriggerActions.CreateRelease)]
    [InlineData(ProjectTriggerTypes.DeploymentTarget, ProjectTriggerActions.AutoDeploy)]
    [InlineData(ProjectTriggerTypes.Scheduled, ProjectTriggerActions.DeployLatestRelease)]
    [InlineData(ProjectTriggerTypes.Scheduled, ProjectTriggerActions.DeployNewRelease)]
    [InlineData(ProjectTriggerTypes.Scheduled, ProjectTriggerActions.DeployLatestReleaseToEnvironment)]
    public void OnlyRegisteredCombinationsAreOfferedAndResolvable(string type, string action)
    {
        var handler = Handler(type, action);
        var registry = new ProjectTriggerRegistry(new[] { handler });

        registry.Capabilities.Single().ShouldBe(new ProjectTriggerCapability(type, action));
        registry.Resolve(type, action).ShouldBeSameAs(handler);
        Should.Throw<ArgumentException>(() => registry.Resolve(type, "Unregistered"));
    }

    [Fact]
    public void EmptyRegistrationPublishesNoTypes()
    {
        var registry = new ProjectTriggerRegistry(Array.Empty<IProjectTriggerHandler>());

        registry.Capabilities.ShouldBeEmpty();
        Should.Throw<ArgumentException>(() => registry.Resolve(ProjectTriggerTypes.GitRepository, ProjectTriggerActions.CreateRelease));
    }

    [Fact]
    public void UnsupportedOrAmbiguousRegistrationsFailAtConstruction()
    {
        var handler = Handler(ProjectTriggerTypes.GitRepository, ProjectTriggerActions.CreateRelease);
        var invalid = Handler(ProjectTriggerTypes.GitRepository, ProjectTriggerActions.AutoDeploy);

        Should.Throw<InvalidOperationException>(() => new ProjectTriggerRegistry(new[] { handler, handler }));
        Should.Throw<InvalidOperationException>(() => new ProjectTriggerRegistry(new[] { invalid }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":0}")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":\"1\"}")]
    [InlineData("{\"schemaVersion\":2,\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"filter\":{\"ref\":1,\"ref\":2}}")]
    [InlineData("[1]")]
    [InlineData("{")]
    public void RejectsAmbiguousOrUnsupportedConfiguration(string json)
        => Should.Throw<ArgumentException>(() => ProjectTriggerConfiguration.Validate(json));

    [Fact]
    public void AcceptsVersionedObjectsWithTypeSpecificFields()
        => ProjectTriggerConfiguration.Validate("""{"schemaVersion":1,"filter":{"credentialId":14},"days":[1,2]}""");

    private static IProjectTriggerHandler Handler(string type, params string[] actions)
    {
        var handler = new Mock<IProjectTriggerHandler>();
        handler.SetupGet(x => x.TriggerType).Returns(type);
        handler.SetupGet(x => x.ActionTypes).Returns(actions);
        return handler.Object;
    }
}
