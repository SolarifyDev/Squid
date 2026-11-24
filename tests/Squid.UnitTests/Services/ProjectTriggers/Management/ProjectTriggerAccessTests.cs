using System.Linq;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.UnitTests.Services.ProjectTriggers.Management;

public class ProjectTriggerAccessTests : IDisposable
{
    private readonly SquidDbContext _db = new(new DbContextOptionsBuilder<SquidDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly List<PermissionCheckRequest> _checks = [];
    private readonly Mock<IAuthorizationService> _authorization = new();
    private readonly ProjectTriggerAccess _access;

    public ProjectTriggerAccessTests()
    {
        _db.AddRange(new UserAccount { Id = 4, UserName = "owner", NormalizedUserName = "OWNER", DisplayName = "Owner", PasswordHash = "" },
            new Project { Id = 7, SpaceId = 2, ProjectGroupId = 3, Name = "P", Slug = "p", Json = "{}", IncludedLibraryVariableSetIds = "[]" },
            new Channel { Id = 10, ProjectId = 7, SpaceId = 2, Name = "Channel", Slug = "channel" },
            new Channel { Id = 11, ProjectId = 8, SpaceId = 2, Name = "Other project", Slug = "other-project" },
            new Channel { Id = 12, ProjectId = 7, SpaceId = 9, Name = "Other space", Slug = "other-space" },
            new Squid.Core.Persistence.Entities.Deployments.Environment { Id = 20, SpaceId = 2, Name = "Test", Slug = "test" },
            new Squid.Core.Persistence.Entities.Deployments.Environment { Id = 30, SpaceId = 2, Name = "Production", Slug = "prod" },
            new Squid.Core.Persistence.Entities.Deployments.Environment { Id = 40, SpaceId = 9, Name = "Outside", Slug = "outside" });
        _db.SaveChanges();
        _authorization.Setup(a => a.EnsurePermissionAsync(It.IsAny<PermissionCheckRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PermissionCheckRequest, CancellationToken>((request, _) => _checks.Add(request)).Returns(Task.CompletedTask);
        var data = new ProjectTriggerDataProvider(new EfRepository(_db), _db);

        _access = new ProjectTriggerAccess(data, _authorization.Object);
    }

    [Theory]
    [InlineData(ProjectTriggerActions.CreateRelease, false, false, true)]
    [InlineData(ProjectTriggerActions.DeployNewRelease, false, true, true)]
    [InlineData(ProjectTriggerActions.DeployLatestRelease, true, true, false)]
    [InlineData(ProjectTriggerActions.DeployLatestReleaseToEnvironment, false, true, false)]
    [InlineData(ProjectTriggerActions.AutoDeploy, true, true, false)]
    public async Task ChecksBoundUserAndProjectGroupForEachAction(string action, bool readSource, bool deploy, bool release)
    {
        var scope = new TriggerResourceScope(10, readSource ? 20 : null, deploy ? 30 : null);

        await _access.EnsureActionAsync(4, Definition(action), scope, default);

        _checks.ShouldAllBe(p => p.UserId == 4 && p.SpaceId == 2 && p.ProjectId == 7 && p.ProjectGroupId == 3);
        _checks.ShouldContain(p => p.Permission == Permission.ProjectView);
        _checks.Any(p => p.Permission == Permission.ReleaseCreate).ShouldBe(release);
        _checks.Any(p => p.Permission == Permission.DeploymentCreate && p.EnvironmentId == 30).ShouldBe(deploy);
        _checks.Any(p => p.Permission == Permission.EnvironmentView && p.EnvironmentId == 20).ShouldBe(readSource);
        _checks.Any(p => p.Permission == Permission.DeploymentView && p.EnvironmentId == 20).ShouldBe(readSource);
    }

    [Fact]
    public async Task AutoDeployRequiresReadAccessToTheDestinationDeploymentHistoryWithoutAnExplicitSource()
    {
        var resources = new TriggerResourceScope(10, null, 30);
        _authorization.Setup(a => a.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(p =>
                p.Permission == Permission.DeploymentView && p.EnvironmentId == 30), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermissionDeniedException(Permission.DeploymentView, "Revoked"));

        var denied = await Should.ThrowAsync<PermissionDeniedException>(() => _access.EnsureActionAsync(4,
            Definition(ProjectTriggerActions.AutoDeploy), resources, default));

        denied.Permission.ShouldBe(Permission.DeploymentView);
    }

    [Theory]
    [InlineData(11, 20, 30)]
    [InlineData(12, 20, 30)]
    [InlineData(99, 20, 30)]
    [InlineData(10, 40, 30)]
    [InlineData(10, 20, 40)]
    [InlineData(10, null, 30)]
    [InlineData(10, 20, null)]
    public async Task RejectsForeignOrMissingResourceReferences(int? channel, int? source, int? destination)
    {
        var definition = Definition(ProjectTriggerActions.DeployLatestRelease);

        await Should.ThrowAsync<ValidationException>(() => _access.EnsureActionAsync(4, definition,
            new TriggerResourceScope(channel, source, destination), default));
    }

    [Fact]
    public async Task ProjectSpaceComesFromStoredProject()
    {
        await Should.ThrowAsync<ProjectTriggerNotFoundException>(() => _access.EnsureActionAsync(4,
            Definition(ProjectTriggerActions.CreateRelease, spaceId: 9), new TriggerResourceScope(10, null, null), default));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(999)]
    public async Task DisabledOrMissingBoundUserCannotRun(int userId)
    {
        var user = await _db.Set<UserAccount>().SingleAsync();
        user.IsDisabled = true;
        await _db.SaveChangesAsync();

        await Should.ThrowAsync<PermissionDeniedException>(() => _access.EnsureActionAsync(userId,
            Definition(ProjectTriggerActions.CreateRelease), new TriggerResourceScope(10, null, null), default));
        _checks.ShouldBeEmpty();
    }

    [Fact]
    public async Task DisabledProjectCannotRun()
    {
        var project = await _db.Set<Project>().SingleAsync();
        project.IsDisabled = true;
        await _db.SaveChangesAsync();

        await Should.ThrowAsync<ValidationException>(() => _access.EnsureActionAsync(4,
            Definition(ProjectTriggerActions.CreateRelease), new TriggerResourceScope(10, null, null), default));
    }

    [Theory]
    [InlineData(Permission.EnvironmentView, 20)]
    [InlineData(Permission.DeploymentView, 20)]
    [InlineData(Permission.DeploymentCreate, 30)]
    public async Task ChecksAgainAfterPermissionRevocation(Permission revoked, int environmentId)
    {
        var definition = Definition(ProjectTriggerActions.DeployLatestRelease);
        var scope = new TriggerResourceScope(10, 20, 30);
        await _access.EnsureActionAsync(4, definition, scope, default);
        _authorization.Setup(a => a.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(p =>
                p.UserId == 4 && p.Permission == revoked && p.EnvironmentId == environmentId), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new PermissionDeniedException(revoked, "Revoked"));

        var denied = await Should.ThrowAsync<PermissionDeniedException>(() => _access.EnsureActionAsync(4, definition, scope, default));

        denied.Permission.ShouldBe(revoked);
    }

    [Fact]
    public async Task UnknownActionCannotRunEvenWithUnrestrictedPermissions()
    {
        await Should.ThrowAsync<ValidationException>(() => _access.EnsureActionAsync(4,
            Definition("FutureAction"), new TriggerResourceScope(10, 20, 30), default));
    }

    private static ProjectTriggerDefinition Definition(string action, int spaceId = 2) =>
        new(7, spaceId, "TestTrigger", action, "{\"schemaVersion\":1}");

    public void Dispose() => _db.Dispose();
}
