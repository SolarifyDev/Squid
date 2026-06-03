using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Events;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Events;

public class AuditDocumentRegistryTests
{
    private readonly AuditDocumentRegistry _registry = new();

    [Fact]
    public void Project_MapsToProjectFeedKey_NameIsProjectName()
    {
        _registry.TryDescribe(new Project { Id = 5, SpaceId = 2, Name = "Checkout" }, out var type, out var keys).ShouldBeTrue();

        type.ShouldBe("Project");
        keys.SpaceId.ShouldBe(2);
        keys.ProjectId.ShouldBe(5);
        keys.ReleaseId.ShouldBeNull();
        keys.Name.ShouldBe("Checkout");
    }

    [Fact]
    public void Release_AlsoCarriesProjectId_SoItShowsOnTheProjectFeed_NameIsVersion()
    {
        _registry.TryDescribe(new Release { Id = 9, ProjectId = 5, SpaceId = 2, Version = "6.6.2" }, out var type, out var keys).ShouldBeTrue();

        type.ShouldBe("Release");
        keys.ReleaseId.ShouldBe(9);
        keys.ProjectId.ShouldBe(5);
        keys.Name.ShouldBe("6.6.2");
    }

    [Fact]
    public void Machine_MapsToMachineFeedKey()
    {
        _registry.TryDescribe(new Machine { Id = 7, SpaceId = 2, Name = "web-01" }, out var type, out var keys).ShouldBeTrue();

        type.ShouldBe("Machine");
        keys.MachineId.ShouldBe(7);
        keys.Name.ShouldBe("web-01");
    }

    [Fact]
    public void Environment_MapsToEnvironmentFeedKey()
    {
        _registry.TryDescribe(new Environment { Id = 3, SpaceId = 2, Name = "Production" }, out var type, out var keys).ShouldBeTrue();

        type.ShouldBe("Environment");
        keys.EnvironmentId.ShouldBe(3);
        keys.Name.ShouldBe("Production");
    }

    [Fact]
    public void UnregisteredEntity_IsNotAudited()
    {
        // The registry is a deliberate allowlist of user-facing documents — being IAuditable
        // or just any object must NOT make an entity part of the audit stream.
        _registry.TryDescribe(new object(), out var type, out var keys).ShouldBeFalse();
        type.ShouldBeNull();
        keys.ShouldBeNull();
    }

    [Fact]
    public void Null_IsNotAudited()
    {
        _registry.TryDescribe(null, out _, out _).ShouldBeFalse();
    }

    [Fact]
    public void RegisteredDocumentTypes_CoverTheUserFacingDocumentSurface()
    {
        AuditDocumentRegistry.RegisteredDocumentTypes.ShouldBe(new[]
        {
            "Project", "Release", "Environment", "Machine", "DeploymentAccount",
            "Channel", "VariableSet", "ProjectGroup", "ExternalFeed", "Certificate"
        }, ignoreOrder: true);
    }
}
