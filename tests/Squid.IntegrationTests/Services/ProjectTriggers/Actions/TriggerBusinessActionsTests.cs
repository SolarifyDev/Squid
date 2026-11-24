using System.Threading;
using Microsoft.EntityFrameworkCore;
using Moq;
using Squid.Core.Persistence.Entities.Account;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Identity;
using Squid.Message.Requests.Deployments.ExternalFeed;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Exceptions;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Message.Constants;
using Squid.Core.Services.ProjectTriggers;
using Squid.Core.Services.ProjectTriggers.Actions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Jobs;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;
using EnvironmentEntity = Squid.Core.Persistence.Entities.Deployments.Environment;

namespace Squid.IntegrationTests.Services.ProjectTriggers.Actions;

public class TriggerBusinessActionsTests() : TestBase("trigger-actions", "squid_test_trigger_actions")
{
    [Fact]
    public async Task SameChannelConcurrentAutomaticReleases_GetDistinctVersionsAndRealSnapshots()
    {
        var seed = await SeedProjectAsync();
        var definition = seed.Definition(ProjectTriggerActions.CreateRelease);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        Task Worker() => Run<SquidDbContext, ITriggerBusinessActions>(async (db, actions) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            if (Interlocked.Increment(ref started) == 2) ready.SetResult();
            await start.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var result = await actions.ExecuteAsync(definition, definition.ActionType, new() { ChannelId = seed.ChannelId }, default);
            result.ReleaseId.ShouldBeGreaterThan(0);
            await transaction.CommitAsync();
        });

        var workers = Task.WhenAll(Worker(), Worker());
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        start.SetResult();
        await workers.WaitAsync(TimeSpan.FromSeconds(30));

        await Run<SquidDbContext>(async db =>
        {
            var releases = await db.Set<Release>().OrderBy(r => r.Version).ToListAsync();
            releases.Select(r => r.Version).ShouldBe(new[] { "1.0.0", "1.0.1" });
            releases.ShouldAllBe(r => r.ProjectDeploymentProcessSnapshotId > 0 && r.ProjectVariableSetSnapshotId > 0);
            (await db.Set<DeploymentProcessSnapshot>().CountAsync()).ShouldBe(1);
            (await db.Set<VariableSetSnapshot>().CountAsync()).ShouldBe(1);
        });
    }

    [Fact]
    public async Task Release_PersistsFrozenPackagesAndNeverCallsTheFeedInTheBusinessTransaction()
    {
        var seed = await SeedProjectAsync();
        var (actorId, feedId) = await SeedFeedPackageAsync();
        var versions = new Mock<IExternalFeedPackageVersionService>(MockBehavior.Strict);
        await using var scope = CurrentScope.BeginLifetimeScope(b =>
        {
            b.RegisterInstance<ICurrentUser>(new Actor(actorId));
            b.RegisterInstance(versions.Object);
        });
        var db = scope.Resolve<SquidDbContext>();
        versions.Setup(x => x.ListVersionsAsync(feedId, "web", It.IsAny<int>(), true, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                db.Database.CurrentTransaction.ShouldBeNull();
                return new SearchFeedPackageVersionsResponseData { Versions = ["2.3.4", "2.2.0"] };
            });
        var definition = seed.Definition(ProjectTriggerActions.CreateRelease);
        var plan = new TriggerActionPlan { ChannelId = seed.ChannelId, Version = "published-23" };
        var resolver = scope.Resolve<ITriggerReleasePackageResolver>();
        await resolver.PrepareAsync(definition, plan, default);
        await using var tx = await db.Database.BeginTransactionAsync();
        await Should.ThrowAsync<InvalidOperationException>(() => resolver.PrepareAsync(definition, new() { ChannelId = seed.ChannelId }, default));
        var result = await scope.Resolve<ITriggerBusinessActions>().ExecuteAsync(definition, definition.ActionType, plan, default);
        await tx.CommitAsync();

        var release = await db.Set<Release>().AsNoTracking().SingleAsync();
        release.Version.ShouldBe("published-23");
        release.Id.ShouldBe(result.ReleaseId);
        var selected = await db.Set<ReleaseSelectedPackage>().AsNoTracking().SingleAsync();
        (selected.ReleaseId, selected.ActionName, selected.PackageReferenceName, selected.FeedId, selected.Version)
            .ShouldBe((release.Id, "Deploy", "web", feedId, "2.3.4"));
        versions.Verify(x => x.ListVersionsAsync(feedId, "web", It.IsAny<int>(), true, null, It.IsAny<CancellationToken>()), Times.Once);
        versions.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DeployNewRelease_CreatesOneReleaseDeploymentAndPendingTaskInsideCallerTransaction()
    {
        var seed = await SeedProjectAsync();
        var definition = seed.Definition(ProjectTriggerActions.DeployNewRelease);
        await Run<SquidDbContext, ITriggerBusinessActions, ISquidBackgroundJobClient>(async (db, actions, jobs) =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            var result = await actions.ExecuteAsync(definition, definition.ActionType, new() { ChannelId = seed.ChannelId, EnvironmentId = seed.EnvironmentId }, default);

            var deployment = await db.Set<Deployment>().SingleAsync();
            deployment.Id.ShouldBe(result.DeploymentId.Value);
            deployment.ReleaseId.ShouldBe(result.ReleaseId);
            deployment.TaskId.ShouldBe(result.TaskId);
            (await db.Set<ServerTask>().SingleAsync()).State.ShouldBe(TaskState.Pending);
            ((MockSquidBackgroundJobClient)jobs).EnqueuedJobs.ShouldBeEmpty();
            await transaction.RollbackAsync();
        });

        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<Deployment>().CountAsync()).ShouldBe(0);
            (await db.Set<Release>().CountAsync()).ShouldBe(0);
            (await db.Set<ServerTask>().CountAsync()).ShouldBe(0);
            (await db.Set<VariableSetSnapshot>().CountAsync()).ShouldBe(0);
        });
    }

    [Fact]
    public async Task DeploymentPrecheckFailure_RollsBackNewReleaseAndSnapshots()
    {
        var seed = await SeedProjectAsync();
        var other = await Run<SquidDbContext, int>(async db =>
        {
            var env = new EnvironmentEntity { Name = "Outside lifecycle", Slug = "outside", SpaceId = 1, Description = "" };
            db.Add(env);
            await db.SaveChangesAsync();
            return env.Id;
        });
        var plan = new TriggerActionPlan { ChannelId = seed.ChannelId, EnvironmentId = other };

        await Should.ThrowAsync<DeploymentValidationException>(() => ExecuteAsync(seed.Definition(ProjectTriggerActions.DeployNewRelease), plan));
        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<Release>().CountAsync()).ShouldBe(0);
            (await db.Set<DeploymentProcessSnapshot>().CountAsync()).ShouldBe(0);
            (await db.Set<ServerTask>().CountAsync()).ShouldBe(0);
        });
    }

    [Theory]
    [InlineData("empty-targets")]
    [InlineData("foreign-machine")]
    [InlineData("wrong-machine-environment")]
    [InlineData("disabled-machine")]
    [InlineData("unhealthy-machine")]
    [InlineData("foreign-release")]
    [InlineData("foreign-channel")]
    [InlineData("foreign-destination")]
    [InlineData("foreign-source")]
    [InlineData("different-catchup-source")]
    public async Task ErroneousPlans_CannotExpandDeploymentScope(string problem)
    {
        var seed = await SeedProjectAsync();
        var created = await ExecuteAsync(seed.Definition(ProjectTriggerActions.CreateRelease), new() { ChannelId = seed.ChannelId });
        var definition = seed.Definition(ProjectTriggerActions.AutoDeploy);
        var baseline = await SeedSuccessAsync(seed, created.ReleaseId);
        var plan = new TriggerActionPlan { ChannelId = seed.ChannelId, EnvironmentId = seed.EnvironmentId, ReleaseId = created.ReleaseId, BaselineDeploymentId = baseline };
        await Run<SquidDbContext>(async db =>
        {
            var machine = await db.Set<Machine>().SingleAsync();
            plan.SpecificMachineIds = [machine.Id];
            switch (problem)
            {
                case "empty-targets": plan.SpecificMachineIds.Clear(); break;
                case "foreign-machine": machine.SpaceId = 2; break;
                case "wrong-machine-environment": machine.EnvironmentIds = "[999]"; break;
                case "disabled-machine": machine.IsDisabled = true; break;
                case "unhealthy-machine": machine.HealthStatus = MachineHealthStatus.Unhealthy; break;
                case "foreign-release": (await db.Set<Release>().SingleAsync()).ProjectId = 999; break;
                case "foreign-channel": (await db.Set<Channel>().SingleAsync()).ProjectId = 999; break;
                case "foreign-destination": (await db.Set<EnvironmentEntity>().SingleAsync()).SpaceId = 2; break;
                case "foreign-source":
                case "different-catchup-source":
                    var other = new EnvironmentEntity { Name = "Source", Slug = "source", Description = "", SpaceId = problem == "foreign-source" ? 2 : 1 };
                    db.Add(other);
                    await db.SaveChangesAsync();
                    plan.SourceEnvironmentId = other.Id;
                    if (problem == "foreign-source") definition = seed.Definition(ProjectTriggerActions.DeployLatestRelease);
                    break;
            }
            await db.SaveChangesAsync();
        });

        var rejection = await Should.ThrowAsync<TriggerActionRejectedException>(() => ExecuteAsync(definition, plan));
        if (problem == "different-catchup-source") rejection.Message.ShouldContain("same environment");
        await Run<SquidDbContext>(async db =>
        {
            (await db.Set<Deployment>().Select(d => d.Id).ToArrayAsync()).ShouldBe(new[] { baseline });
            (await db.Set<ServerTask>().CountAsync()).ShouldBe(1);
        });
    }

    [Fact]
    public async Task AutoDeploy_PersistsOnlyNamedMachinesAndPreservesGuidedFailure()
    {
        var seed = await SeedProjectAsync();
        var created = await ExecuteAsync(seed.Definition(ProjectTriggerActions.CreateRelease), new() { ChannelId = seed.ChannelId });
        var definition = seed.Definition(ProjectTriggerActions.AutoDeploy);
        var plan = new TriggerActionPlan
        {
            EnvironmentId = seed.EnvironmentId, ReleaseId = created.ReleaseId, SpecificMachineIds = [seed.MachineId], UseGuidedFailure = true
        };
        (await Should.ThrowAsync<TriggerActionRejectedException>(() => ExecuteAsync(definition, plan))).Message.ShouldContain("successful baseline");
        await SeedSuccessAsync(seed, created.ReleaseId);
        var result = await ExecuteAsync(definition, plan);

        await Run<SquidDbContext>(async db =>
        {
            var deployment = await db.Set<Deployment>().SingleAsync(d => d.Id == result.DeploymentId);
            var payload = DeploymentTargetFinder.ParseRequestPayload(deployment.Json);
            payload.SpecificMachineIds.ShouldBe(new[] { seed.MachineId });
            payload.UseGuidedFailure.ShouldBeTrue();
            result.ReleaseId.ShouldBe(created.ReleaseId);
            (await db.Set<Release>().CountAsync()).ShouldBe(1);
        });
    }

    [Fact]
    public async Task PromotionsRequireSuccessfulSourceHistoryAndHonorTheFrozenBaseline()
    {
        var seed = await SeedProjectAsync();
        var v1 = await ExecuteAsync(seed.Definition(ProjectTriggerActions.CreateRelease), new() { ChannelId = seed.ChannelId });
        var v2 = await ExecuteAsync(seed.Definition(ProjectTriggerActions.CreateRelease), new() { ChannelId = seed.ChannelId });
        var plan = new TriggerActionPlan { SourceEnvironmentId = seed.EnvironmentId, EnvironmentId = seed.EnvironmentId, ReleaseId = v1.ReleaseId };
        var promotion = seed.Definition(ProjectTriggerActions.DeployLatestRelease);
        await Should.ThrowAsync<TriggerActionRejectedException>(() => ExecuteAsync(promotion, plan));

        var baseline = await SeedSuccessAsync(seed, v1.ReleaseId, DateTimeOffset.UtcNow.AddMinutes(-5));
        await SeedSuccessAsync(seed, v2.ReleaseId);
        plan.BaselineDeploymentId = baseline;
        var result = await ExecuteAsync(promotion, plan);
        result.ReleaseId.ShouldBe(v1.ReleaseId);
        plan.ReleaseId = v2.ReleaseId;
        await Should.ThrowAsync<TriggerActionRejectedException>(() => ExecuteAsync(promotion, plan));
    }

    [Fact]
    public async Task NewReleaseRequiresCallerTransactionAndConsistentActionType()
    {
        var seed = await SeedProjectAsync();
        var definition = seed.Definition(ProjectTriggerActions.CreateRelease);
        var plan = new TriggerActionPlan { ChannelId = seed.ChannelId };
        await Run<SquidDbContext, ITriggerBusinessActions>(async (db, actions) =>
        {
            await Should.ThrowAsync<InvalidOperationException>(() => actions.ExecuteAsync(definition, definition.ActionType, plan, default));
            await using var tx = await db.Database.BeginTransactionAsync();
            await Should.ThrowAsync<TriggerActionRejectedException>(() => actions.ExecuteAsync(definition, ProjectTriggerActions.AutoDeploy, plan, default));
            (await db.Set<Release>().AnyAsync()).ShouldBeFalse();
        });
    }

    [Fact]
    public async Task SuccessfulHistory_FiltersScopeChannelAndTargetRunsAndOrdersCompletionTiesDeterministically()
    {
        var seed = await SeedProjectAsync();
        await using var scope = CurrentScope.BeginLifetimeScope();
        var db = scope.Resolve<SquidDbContext>();
        var builder = new TestDataBuilder(scope.Resolve<IRepository>(), db);
        var blue = await builder.CreateChannelAsync(seed.ProjectId, 1, "Blue");
        var v18 = await builder.CreateReleaseAsync(seed.ProjectId, seed.ChannelId, "1.8");
        var v19 = await builder.CreateReleaseAsync(seed.ProjectId, seed.ChannelId, "1.9");
        var blue18 = await builder.CreateReleaseAsync(seed.ProjectId, blue.Id, "1.8");
        var instant = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        async Task<Deployment> Complete(Release release, int minutes, string state = TaskState.Success, int journalSpace = 1,
            int? deploymentChannel = null, string taskState = TaskState.Success)
        {
            var task = await builder.CreateServerTaskAsync(taskState);
            task.ProjectId = seed.ProjectId;
            task.EnvironmentId = seed.EnvironmentId;
            var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.EnvironmentId, release.Id,
                task.Id, deploymentChannel ?? release.ChannelId);
            db.Add(new DeploymentCompletion { SpaceId = journalSpace, DeploymentId = deployment.Id, CompletedTime = instant.AddMinutes(minutes), State = state });
            await db.SaveChangesAsync();
            return deployment;
        }

        await Complete(v18, 1);
        var stable = await Complete(v18, 1);
        stable.Json = """{"specificMachineIds":null,"ExcludedMachineIds":[],"SkipActionIds":[44,55]}""";
        stable.ProcessSnapshotId = 778;
        stable.VariableSetSnapshotId = 779;
        var latest = await Complete(blue18, 2);
        await Complete(v19, 3, TaskState.Failed);
        await Complete(v19, 4, journalSpace: 2);
        await Complete(v19, 5, deploymentChannel: blue.Id);
        await Complete(v19, 6, taskState: TaskState.Executing);
        var target = await Complete(v18, 7);
        (await Complete(v19, 8)).Json = """{"sPeCiFiCMachineIds":["123"]}""";
        (await Complete(v19, 9)).Json = """{"excludedmachineids":[456]}""";
        (await Complete(v19, 10)).MachineId = seed.MachineId;
        (await Complete(v19, 11)).Json = """{"SpecificMachineIds":"unreadable"}""";
        var trigger = new ProjectTrigger
        {
            SpaceId = 1, ProjectId = seed.ProjectId, TriggerType = "Test", ActionType = ProjectTriggerActions.AutoDeploy,
            Name = "Repair", ConfigurationJson = "{}", ExecutionUserId = 1
        };
        db.Add(trigger);
        await db.SaveChangesAsync();
        db.Add(new ProjectTriggerRun
        {
            TriggerId = trigger.Id, Revision = 1, ExecutionUserId = 1, TriggerType = trigger.TriggerType, ActionType = trigger.ActionType,
            ConfigurationJson = "{}", SourceKey = "machine", OccurrenceKey = "1", EvidenceJson = "{}", Status = ProjectTriggerRunStatuses.Dispatched,
            ObservedAt = instant, NextAttemptAt = instant, ReleaseId = v18.Id, DeploymentId = target.Id
        });
        await db.SaveChangesAsync();

        var history = scope.Resolve<ITriggerDeploymentHistory>();
        var anyChannel = await history.FindLatestSuccessfulAsync(1, seed.ProjectId, seed.EnvironmentId);
        anyChannel.DeploymentId.ShouldBe(latest.Id);
        var baseline = await history.FindLatestSuccessfulAsync(1, seed.ProjectId, seed.EnvironmentId, seed.ChannelId);
        baseline.DeploymentId.ShouldBe(stable.Id);
        baseline.ReleaseId.ShouldBe(v18.Id);
        baseline.RequestJson.ShouldBe(stable.Json);
        baseline.ProcessSnapshotId.ShouldBe(778);
        baseline.VariableSetSnapshotId.ShouldBe(779);
        (await history.FindLatestSuccessfulAsync(2, seed.ProjectId, seed.EnvironmentId)).ShouldBeNull();
        (await history.FindLatestSuccessfulAsync(1, 999, seed.EnvironmentId)).ShouldBeNull();
        (await history.FindLatestSuccessfulAsync(1, seed.ProjectId, 999)).ShouldBeNull();
    }

    private Task<(int ActorId, int FeedId)> SeedFeedPackageAsync() => Run<SquidDbContext, (int, int)>(async db =>
    {
        var actor = new UserAccount { UserName = "operator", NormalizedUserName = "OPERATOR", DisplayName = "Operator", PasswordHash = "" };
        var role = new UserRole { Name = "Feed reader" };
        var team = new Team { Name = "Release operators", SpaceId = 1 };
        var feed = new ExternalFeed { SpaceId = 1, Name = "Packages", Slug = "packages", FeedType = "NuGet", FeedUri = "https://example.invalid/packages" };
        db.AddRange(actor, role, team, feed);
        await db.SaveChangesAsync();
        db.AddRange(new TeamMember { TeamId = team.Id, UserId = actor.Id },
            new ScopedUserRole { SpaceId = 1, TeamId = team.Id, UserRoleId = role.Id },
            new UserRolePermission { UserRoleId = role.Id, Permission = nameof(Permission.FeedView) });
        var action = await db.Set<DeploymentAction>().SingleAsync();
        var builder = new TestDataBuilder(new EfRepository(db), db);
        await builder.CreateActionPropertiesAsync(action.Id, (SpecialVariables.Action.PackageFeedId, feed.Id.ToString()), (SpecialVariables.Action.PackageId, "web"));
        return (actor.Id, feed.Id);
    });

    private sealed record Actor(int UserId) : ICurrentUser
    {
        public int? Id => UserId;
        public string Name => "Release operator";
        public bool IsInternal => false;
    }

    private Task<int> SeedSuccessAsync(Seed seed, int release, DateTimeOffset? at = null) => Run<SquidDbContext, int>(async db =>
    {
        var builder = new TestDataBuilder(new EfRepository(db), db);
        var task = await builder.CreateServerTaskAsync();
        task.ProjectId = seed.ProjectId;
        task.EnvironmentId = seed.EnvironmentId;
        var deployment = await builder.CreateDeploymentAsync(seed.ProjectId, seed.EnvironmentId, release, task.Id, seed.ChannelId);
        await builder.CreateDeploymentCompletionAsync(deployment.Id, completedTime: at);
        return deployment.Id;
    });

    private async Task<TriggerActionResult> ExecuteAsync(ProjectTriggerDefinition definition, TriggerActionPlan plan)
    {
        await using var scope = CurrentScope.BeginLifetimeScope();
        var db = scope.Resolve<SquidDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await scope.Resolve<ITriggerBusinessActions>().ExecuteAsync(definition, definition.ActionType, plan, default);
        await transaction.CommitAsync();
        return result;
    }

    private async Task<Seed> SeedProjectAsync()
    {
        await using var scope = CurrentScope.BeginLifetimeScope();
        var db = scope.Resolve<SquidDbContext>();
        var builder = new TestDataBuilder(scope.Resolve<IRepository>(), db);
        var lifecycle = await builder.CreateLifecycleAsync();
        var env = await builder.CreateEnvironmentAsync("Test");
        await builder.CreateLifecyclePhaseAsync(lifecycle.Id, env.Id);
        var variableSet = await builder.CreateVariableSetAsync();
        var project = await builder.CreateProjectAsync(variableSet.Id, 0, 1, lifecycle.Id);
        var channel = await db.Set<Channel>().SingleAsync(c => c.ProjectId == project.Id);
        var process = await builder.CreateDeploymentProcessAsync();
        process.ProjectId = project.Id;
        await builder.UpdateProjectProcessIdAsync(project, process.Id);
        await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id);
        var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Web");
        var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Deploy");
        await builder.CreateActionPropertiesAsync(action.Id, (SpecialVariables.Action.ScriptBody, "echo ready"));
        var machine = await builder.CreateMachineAsync(env.Id, "web", "web", """{"CommunicationStyle":"KubernetesApi"}""");
        machine.HealthStatus = MachineHealthStatus.Healthy;
        await db.SaveChangesAsync();
        return new Seed(project.Id, channel.Id, env.Id, machine.Id);
    }

    private sealed record Seed(int ProjectId, int ChannelId, int EnvironmentId, int MachineId)
    {
        public ProjectTriggerDefinition Definition(string action) => new(ProjectId, 1, "Test", action, "{}");
    }
}
