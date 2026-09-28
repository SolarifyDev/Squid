using Microsoft.EntityFrameworkCore;
using System.Linq;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using DeploymentEnvironment = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportDestinationDataProviderTests
{
    [Fact]
    public async Task GetResourcesAsync_ReturnsSupportedResourcesFromDestinationSpaceAndGlobalTeams()
    {
        await using var db = CreateDbContext();
        var provider = new OctopusImportDestinationDataProvider(new EfRepository(db));

        db.Set<Project>().AddRange(
            new Project { Id = 1, SpaceId = 7, Name = "Project", Slug = "project" },
            new Project { Id = 2, SpaceId = 8, Name = "Other Project", Slug = "other-project" });
        db.Set<ProjectGroup>().Add(new ProjectGroup { Id = 3, SpaceId = 7, Name = "Group", Slug = "group" });
        db.Set<DeploymentEnvironment>().Add(new DeploymentEnvironment { Id = 4, SpaceId = 7, Name = "Development", Slug = "development" });
        db.Set<Lifecycle>().Add(new Lifecycle { Id = 5, SpaceId = 7, Name = "Default Lifecycle", Slug = "default-lifecycle" });
        db.Set<ExternalFeed>().Add(new ExternalFeed
        {
            Id = 6,
            SpaceId = 7,
            Name = "Docker",
            Slug = "docker",
            Password = "must-not-be-read"
        });
        db.Set<Team>().AddRange(
            new Team { Id = 7, SpaceId = 7, Name = "Operators" },
            new Team { Id = 8, SpaceId = 0, Name = "Administrators", IsBuiltIn = true },
            new Team { Id = 9, SpaceId = 8, Name = "Other Team" });
        db.Set<Machine>().Add(new Machine
        {
            Id = 10,
            SpaceId = 7,
            Name = "Worker",
            Slug = "worker",
            Endpoint = """{"Password":"must-not-be-read"}"""
        });
        db.Set<DeploymentAccount>().Add(new DeploymentAccount
        {
            Id = 11,
            SpaceId = 7,
            Name = "AWS",
            Slug = "aws",
            Credentials = """{"SecretKey":"must-not-be-read"}"""
        });
        await db.SaveChangesAsync();

        var resources = await provider.GetResourcesAsync(7);

        resources.Count.ShouldBe(9);
        resources.Select(x => (x.Kind, x.Id)).ShouldBe(
        [
            (OctopusResourceKind.Project, 1),
            (OctopusResourceKind.ProjectGroup, 3),
            (OctopusResourceKind.Environment, 4),
            (OctopusResourceKind.Lifecycle, 5),
            (OctopusResourceKind.Feed, 6),
            (OctopusResourceKind.Team, 7),
            (OctopusResourceKind.Team, 8),
            (OctopusResourceKind.Machine, 10),
            (OctopusResourceKind.Account, 11)
        ]);
        resources.All(x => x.SpaceId == 0 || x.SpaceId == 7).ShouldBeTrue();
    }

    private static SquidDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        return new SquidDbContext(options);
    }
}
