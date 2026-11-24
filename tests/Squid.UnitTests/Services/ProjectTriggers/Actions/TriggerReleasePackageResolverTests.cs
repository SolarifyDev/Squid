using System.Linq;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Authorization;
using Squid.Core.Services.Authorization.Exceptions;
using Squid.Core.Services.Identity;
using Squid.Core.Services.ProjectTriggers.Management;
using Squid.Message.Enums;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Actions;
using Squid.Message.Commands.Deployments.Release;
using Squid.Message.Constants;
using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Requests.Deployments.ExternalFeed;

namespace Squid.UnitTests.Services.ProjectTriggers.Actions;

public class TriggerReleasePackageResolverTests
{
    private const int Actor = 41;
    private readonly Mock<IExternalFeedPackageVersionService> _versions = new(MockBehavior.Strict);
    private readonly Mock<IAuthorizationService> _auth = new(MockBehavior.Strict);
    private bool _feedAllowed = true;

    public TriggerReleasePackageResolverTests()
    {
        _auth.Setup(a => a.EnsurePermissionAsync(It.Is<PermissionCheckRequest>(r => r.UserId == Actor && r.Permission == Permission.FeedView
            && r.SpaceId == 1 && r.ProjectId == 1 && r.EnvironmentId == null), It.IsAny<CancellationToken>()))
            .Returns(() => _feedAllowed ? Task.CompletedTask : Task.FromException(new PermissionDeniedException(Permission.FeedView, "revoked")));
    }

    private static readonly ProjectTriggerDefinition Definition = new(1, 1, "Test", ProjectTriggerActions.CreateRelease, "{}");

    [Fact]
    public async Task Prepare_PinsEveryApplicableDependencyUsingChannelRulesAndObservedVersions()
    {
        using var db = await SeedAsync();
        var resolver = Resolver(db);
        var plan = new TriggerActionPlan { ChannelId = 2, SelectedPackages = [Selection("Observed", "event", "1.5.0")] };
        var lookups = ReturnVersions("web", "3.0.0", "latest", "1.9.0", "1.0.0");
        var observed = ReturnVersions("event", "1.7.0", "1.5.0");

        await resolver.PrepareAsync(Definition, plan, default);

        plan.SelectedPackages.Select(p => (p.ActionName, p.Version)).ShouldBe(new[] { ("Install", "1.9.0"), ("Observed", "1.5.0") });
        plan.PackageReferenceFingerprint.ShouldNotBeNullOrWhiteSpace();
        await resolver.ValidateAsync(Definition, plan, default);
        _versions.Verify(lookups, Times.Once);
        _versions.Verify(observed, Times.Once);
        _versions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Install", "web", 99, "1.0.0")]
    [InlineData("Unrelated", "web", 6, "1.0.0")]
    [InlineData("Install", "web", 6, "3.0.0")]
    [InlineData("Install", "web", 6, " ")]
    public async Task Prepare_RejectsForeignUnknownOrRuleViolatingObservedPins(string action, string package, int feed, string version)
    {
        using var db = await SeedAsync();
        var plan = new TriggerActionPlan { ChannelId = 2, SelectedPackages = [Selection(action, package, version, feed)] };

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver(db).PrepareAsync(Definition, plan, default));
        _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Prepare_RejectsCrossSpaceFeedBeforeAnyExternalRequest()
    {
        using var db = await SeedAsync();
        var feed = await db.Set<ExternalFeed>().SingleAsync();
        feed.SpaceId = 99;
        await db.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver(db).PrepareAsync(Definition, new() { ChannelId = 2 }, default));
        _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Validate_RejectsChangedFeedOrMissingSelectionsWithoutLookingUpNewVersions()
    {
        using var db = await SeedAsync();
        var resolver = Resolver(db);
        var plan = new TriggerActionPlan { ChannelId = 2, SelectedPackages = [Selection("Install", "web", "1.8.0"), Selection("Observed", "event", "1.0.0")] };
        var web = ReturnVersions("web", "1.8.0");
        var observed = ReturnVersions("event", "1.0.0");
        await resolver.PrepareAsync(Definition, plan, default);
        _versions.Verify(web, Times.Once);
        _versions.Verify(observed, Times.Once);

        plan.SelectedPackages.RemoveAt(0);
        await Should.ThrowAsync<InvalidOperationException>(() => resolver.ValidateAsync(Definition, plan, default));
        plan.SelectedPackages.Add(Selection("Install", "web", "1.8.0"));
        var property = await db.Set<DeploymentActionProperty>().SingleAsync(p => p.ActionId == 10 && p.PropertyName == SpecialVariables.Action.PackageId);
        property.PropertyValue = "replacement";
        await db.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => resolver.ValidateAsync(Definition, plan, default));
        _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Prepare_ReportsMissingCompatibleVersionAndLeavesPlanUnchanged()
    {
        using var db = await SeedAsync();
        var plan = new TriggerActionPlan { ChannelId = 2 };
        ReturnVersions("web", "3.0.0");

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Resolver(db).PrepareAsync(Definition, plan, default));

        error.Message.ShouldContain("version");
        error.Message.ShouldNotContain("web");
        plan.SelectedPackages.ShouldBeEmpty();
        plan.PackageReferenceFingerprint.ShouldBeNull();
    }

    [Fact]
    public async Task Prepare_RejectsObservedVersionMissingFromTheBoundPackage()
    {
        using var db = await SeedAsync();
        var plan = new TriggerActionPlan { ChannelId = 2, SelectedPackages = [Selection("Install", "web", "1.8.0")] };
        ReturnVersions("web", "1.9.0");

        (await Should.ThrowAsync<TriggerActionRejectedException>(() => Resolver(db).PrepareAsync(Definition, plan, default))).Message.ShouldContain("version");
    }

    [Fact]
    public async Task Prepare_RejectsInvalidChannelRulesBeforeLookingUpVersions()
    {
        using var db = await SeedAsync();
        (await db.Set<ChannelVersionRule>().SingleAsync()).VersionRange = "[invalid";
        await db.SaveChangesAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => Resolver(db).PrepareAsync(Definition, new() { ChannelId = 2 }, default));
        _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Prepare_RejectsAmbiguousDependencyNamesAndDuplicatePins()
    {
        using var db = await SeedAsync();
        var pin = Selection("Install", "web", "1.0.0");
        var plan = new TriggerActionPlan { ChannelId = 2, SelectedPackages = [pin, pin] };
        await Should.ThrowAsync<InvalidOperationException>(() => Resolver(db).PrepareAsync(Definition, plan, default));

        AddAction(db, 14, "INSTALL", "web");
        await db.SaveChangesAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => Resolver(db).PrepareAsync(Definition, new() { ChannelId = 2 }, default));
        _versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task RevokedFeedView_PreventsLookupAndRejectsAnAlreadyPreparedRelease()
    {
        using var db = await SeedAsync();
        var resolver = Resolver(db);
        var plan = new TriggerActionPlan { ChannelId = 2 };
        var web = ReturnVersions("web", "1.0.0");
        var observed = ReturnVersions("event", "1.0.0");
        await resolver.PrepareAsync(Definition, plan, default);
        _feedAllowed = false;

        await Should.ThrowAsync<PermissionDeniedException>(() => resolver.PrepareAsync(Definition, new() { ChannelId = 2 }, default));
        await Should.ThrowAsync<PermissionDeniedException>(() => resolver.ValidateAsync(Definition, plan, default));
        _versions.Verify(web, Times.Once);
        _versions.Verify(observed, Times.Once);
        _versions.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null, false, false)]
    [InlineData(Actor, true, false)]
    [InlineData(CurrentUsers.InternalUser.Id, false, false)]
    [InlineData(Actor, false, true)]
    public async Task BoundActiveNonInternalUserRequiredBeforeFeedAccess(int? id, bool internalUser, bool disabled)
    {
        using var db = await SeedAsync();
        var user = await db.Set<UserAccount>().SingleAsync();
        user.IsDisabled = disabled;
        await db.SaveChangesAsync();
        var resolver = Resolver(db, new TestUser(id, internalUser));

        await Should.ThrowAsync<PermissionDeniedException>(() => resolver.PrepareAsync(Definition, new() { ChannelId = 2 }, default));
        _versions.VerifyNoOtherCalls();
        _auth.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PackageFreeProcessDoesNotRequireFeedView()
    {
        using var db = await SeedAsync();
        foreach (var step in db.Set<DeploymentStep>()) step.IsDisabled = true;
        await db.SaveChangesAsync();
        _feedAllowed = false;
        var resolver = Resolver(db);
        var plan = new TriggerActionPlan { ChannelId = 2 };

        await resolver.PrepareAsync(Definition, plan, default);
        await resolver.ValidateAsync(Definition, plan, default);
        _auth.VerifyNoOtherCalls();
        _versions.VerifyNoOtherCalls();
    }

    private TriggerReleasePackageResolver Resolver(SquidDbContext db, ICurrentUser user = null)
    {
        var repository = new EfRepository(db);

        return new(new TriggerActionDataProvider(repository), _versions.Object,
            new ChannelVersionRuleDataProvider(repository), new ChannelVersionRuleValidator(),
            user ?? new TestUser(Actor, false),
            new ProjectTriggerAccess(new ProjectTriggerDataProvider(repository, db), _auth.Object));
    }

    private sealed record TestUser(int? Id, bool IsInternal) : ICurrentUser
    {
        public string Name => "Trigger actor";
    }

    private System.Linq.Expressions.Expression<Func<IExternalFeedPackageVersionService, Task<SearchFeedPackageVersionsResponseData>>> ReturnVersions(string package, params string[] versions)
    {
        System.Linq.Expressions.Expression<Func<IExternalFeedPackageVersionService, Task<SearchFeedPackageVersionsResponseData>>> lookup =
            s => s.ListVersionsAsync(6, package, It.IsAny<int>(), true, null, It.IsAny<CancellationToken>());
        _versions.Setup(lookup).ReturnsAsync(new SearchFeedPackageVersionsResponseData { Versions = versions.ToList() });
        return lookup;
    }

    private static CreateReleaseSelectedPackageDto Selection(string action, string package, string version, int feed = 6) => new()
    {
        ActionName = action, PackageReferenceName = package, FeedId = feed, Version = version
    };

    private static async Task<SquidDbContext> SeedAsync()
    {
        var options = new DbContextOptionsBuilder<SquidDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString(), b => b.EnableNullChecks(false)).Options;
        var db = new SquidDbContext(options);
        db.AddRange(new UserAccount { Id = Actor }, new Project { Id = 1, SpaceId = 1, DeploymentProcessId = 3 }, new Channel { Id = 2, ProjectId = 1, SpaceId = 1 },
            new DeploymentProcess { Id = 3, SpaceId = 1, ProjectId = 1 }, new DeploymentStep { Id = 4, ProcessId = 3 },
            new ExternalFeed { Id = 6, SpaceId = 1, FeedType = "NuGet", FeedUri = "https://example.invalid/feed" },
            new ChannelVersionRule { ChannelId = 2, VersionRange = "[1.0,2.0)" });
        AddAction(db, 10, "Install", "web");
        AddAction(db, 11, "Observed", "event");
        AddAction(db, 12, "OtherChannel", "not-used");
        db.Add(new ActionChannel { ActionId = 12, ChannelId = 42 });
        AddAction(db, 13, "Disabled", "disabled");
        db.Set<DeploymentAction>().Local.Single(a => a.Id == 13).IsDisabled = true;
        await db.SaveChangesAsync();
        return db;
    }

    private static void AddAction(SquidDbContext db, int id, string action, string package)
    {
        db.AddRange(new DeploymentAction { Id = id, StepId = 4, Name = action },
            new DeploymentActionProperty { ActionId = id, PropertyName = SpecialVariables.Action.PackageId, PropertyValue = package },
            new DeploymentActionProperty { ActionId = id, PropertyName = SpecialVariables.Action.PackageFeedId, PropertyValue = "6" });
    }
}
