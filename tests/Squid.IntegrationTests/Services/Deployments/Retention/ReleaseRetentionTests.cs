using System;
using System.Threading;
using System.Threading.Tasks;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.LifeCycle;
using Squid.IntegrationTests.Base;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.Deployments;
using ProjectEntity = Squid.Core.Persistence.Entities.Deployments.Project;

namespace Squid.IntegrationTests.Services.Deployments.Retention;

/// <summary>
/// Integration coverage (real Postgres) for release-level retention: when a lifecycle has a
/// finite release-retention window, <c>RetentionPolicyEnforcer</c> prunes releases that are
/// past the window, not currently deployed, and have no surviving deployments — cascading
/// their <c>ReleaseSelectedPackage</c> rows and ref-count-GCing their process/variable
/// snapshots (a snapshot shared by a surviving release/deployment is kept). KeepForever
/// (the lifecycle default) prunes nothing, so the feature is opt-in / non-breaking.
/// Each test seeds its own isolated project graph.
/// </summary>
public class ReleaseRetentionTests : TestBase
{
    public ReleaseRetentionTests() : base("ReleaseRetention", "squid_it_release_retention")
    {
    }

    [Fact]
    public async Task OldUndeployedRelease_PrunedWithPackagesAndSnapshots()
    {
        var graph = await SeedGraphAsync(keepForever: false).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var releaseId = await SeedReleaseAsync(graph, ageDays: 40, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(releaseId).ConfigureAwait(false)).ShouldBeFalse(
            customMessage: "An old, undeployed release past the window must be pruned.");
        (await PackageCountAsync(releaseId).ConfigureAwait(false)).ShouldBe(0, customMessage: "ReleaseSelectedPackage rows must cascade with the release.");
        (await ProcessSnapshotExistsAsync(snap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Orphaned process snapshot must be GC'd.");
        (await VariableSnapshotExistsAsync(snap.VariableSnapshotId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Orphaned variable snapshot must be GC'd.");
    }

    [Fact]
    public async Task RecentRelease_Kept()
    {
        var graph = await SeedGraphAsync(keepForever: false).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var releaseId = await SeedReleaseAsync(graph, ageDays: 5, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(releaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ProcessSnapshotExistsAsync(snap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
        (await VariableSnapshotExistsAsync(snap.VariableSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task CurrentlyDeployedOldRelease_Kept()
    {
        var graph = await SeedGraphAsync(keepForever: false).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var releaseId = await SeedReleaseAsync(graph, ageDays: 40, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);
        await SeedDeploymentForReleaseAsync(graph, releaseId, withSuccessCompletion: true).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(releaseId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A currently-deployed release must be preserved even when old.");
        (await ProcessSnapshotExistsAsync(snap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task OldReleaseWithSurvivingDeployment_Kept()
    {
        var graph = await SeedGraphAsync(keepForever: false).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var releaseId = await SeedReleaseAsync(graph, ageDays: 40, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);
        await SeedDeploymentForReleaseAsync(graph, releaseId, withSuccessCompletion: false).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(releaseId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A release with a surviving (recent) deployment must be kept.");
    }

    [Fact]
    public async Task SharedSnapshot_KeptWhileAnotherReleaseStillReferencesIt()
    {
        var graph = await SeedGraphAsync(keepForever: false).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);

        // Two old releases share the SAME snapshots. One is pruned (undeployed), the other is
        // preserved (currently deployed) — so the shared snapshots must survive (ref-count).
        var prunedReleaseId = await SeedReleaseAsync(graph, ageDays: 40, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);
        var keptReleaseId = await SeedReleaseAsync(graph, ageDays: 40, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);
        await SeedDeploymentForReleaseAsync(graph, keptReleaseId, withSuccessCompletion: true).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(prunedReleaseId).ConfigureAwait(false)).ShouldBeFalse();
        (await ReleaseExistsAsync(keptReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ProcessSnapshotExistsAsync(snap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A snapshot still referenced by a surviving release must NOT be GC'd.");
        (await VariableSnapshotExistsAsync(snap.VariableSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task KeepForeverLifecycle_PrunesNothing()
    {
        var graph = await SeedGraphAsync(keepForever: true).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var releaseId = await SeedReleaseAsync(graph, ageDays: 400, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(releaseId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "KeepForever (the lifecycle default) must prune no releases — opt-in / non-breaking.");
        (await ProcessSnapshotExistsAsync(snap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    // ── enforce / assertion helpers ──

    private Task EnforceAsync(int projectId)
        => Run<IRetentionPolicyEnforcer>(enforcer => enforcer.EnforceRetentionForProjectAsync(projectId, CancellationToken.None));

    private Task<bool> ReleaseExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<Release>(r => r.Id == id, CancellationToken.None));

    private Task<int> PackageCountAsync(int releaseId)
        => Run<IRepository, int>(repo => repo.CountAsync<ReleaseSelectedPackage>(p => p.ReleaseId == releaseId, CancellationToken.None));

    private Task<bool> ProcessSnapshotExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<DeploymentProcessSnapshot>(s => s.Id == id, CancellationToken.None));

    private Task<bool> VariableSnapshotExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<VariableSetSnapshot>(s => s.Id == id, CancellationToken.None));

    // ── seeding helpers ──

    private async Task<(int ProjectId, int EnvironmentId, int ChannelId)> SeedGraphAsync(bool keepForever, int windowDays = 30)
    {
        var graph = (ProjectId: 0, EnvironmentId: 0, ChannelId: 0);

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var suffix = Guid.NewGuid().ToString("N");

            var environment = await builder.CreateEnvironmentAsync($"env-{suffix}").ConfigureAwait(false);

            var lifecycle = new Lifecycle
            {
                Name = $"lifecycle-{suffix}",
                SpaceId = 1,
                Slug = $"lifecycle-{suffix}",
                ReleaseRetentionKeepForever = keepForever,
                ReleaseRetentionUnit = RetentionPolicyUnit.Days,
                ReleaseRetentionQuantity = windowDays,
                TentacleRetentionKeepForever = true
            };
            await repo.InsertAsync(lifecycle, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await builder.CreateLifecyclePhaseAsync(lifecycle.Id, environment.Id, $"phase-{suffix}").ConfigureAwait(false);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);

            var project = new ProjectEntity
            {
                Name = $"project-{suffix}",
                Slug = $"project-{suffix}",
                IsDisabled = false,
                VariableSetId = variableSet.Id,
                DeploymentProcessId = 0,
                ProjectGroupId = 1,
                LifecycleId = lifecycle.Id,
                AutoCreateRelease = false,
                Json = string.Empty,
                IncludedLibraryVariableSetIds = "[]",
                DiscreteChannelRelease = false,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                AllowIgnoreChannelRules = false
            };
            await repo.InsertAsync(project, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, lifecycle.Id, $"channel-{suffix}").ConfigureAwait(false);

            graph = (project.Id, environment.Id, channel.Id);
        }).ConfigureAwait(false);

        return graph;
    }

    private async Task<(int ProcessSnapshotId, int VariableSnapshotId)> SeedSnapshotPairAsync()
    {
        var ids = (ProcessSnapshotId: 0, VariableSnapshotId: 0);

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var processSnapshot = new DeploymentProcessSnapshot
            {
                OriginalProcessId = 0,
                Version = 1,
                SnapshotData = new byte[] { 1 },
                ContentHash = $"proc-{Guid.NewGuid():N}",
                UncompressedSize = 1,
                LastModifiedDate = DateTimeOffset.UtcNow
            };
            await repo.InsertAsync(processSnapshot, CancellationToken.None).ConfigureAwait(false);

            var variableSnapshot = new VariableSetSnapshot
            {
                SnapshotData = new byte[] { 1 },
                ContentHash = $"var-{Guid.NewGuid():N}",
                UncompressedSize = 1,
                LastModifiedDate = DateTimeOffset.UtcNow
            };
            await repo.InsertAsync(variableSnapshot, CancellationToken.None).ConfigureAwait(false);

            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            ids = (processSnapshot.Id, variableSnapshot.Id);
        }).ConfigureAwait(false);

        return ids;
    }

    private async Task<int> SeedReleaseAsync((int ProjectId, int EnvironmentId, int ChannelId) graph, int ageDays, int processSnapshotId, int variableSnapshotId, bool withPackage = true)
    {
        var releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var release = new Release
            {
                Version = $"1.0.{Guid.NewGuid():N}",
                ProjectId = graph.ProjectId,
                ChannelId = graph.ChannelId,
                ProjectDeploymentProcessSnapshotId = processSnapshotId,
                ProjectVariableSetSnapshotId = variableSnapshotId,
                SpaceId = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                LastModifiedDate = DateTimeOffset.UtcNow
            };
            await repo.InsertAsync(release, CancellationToken.None).ConfigureAwait(false);
            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            releaseId = release.Id;

            if (withPackage)
            {
                await repo.InsertAsync(new ReleaseSelectedPackage
                {
                    ReleaseId = releaseId,
                    FeedId = 1,
                    ActionName = "action",
                    PackageReferenceName = string.Empty,
                    Version = "1.0.0"
                }, CancellationToken.None).ConfigureAwait(false);

                await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            }

            await repo.ExecuteUpdateAsync<Release>(
                r => r.Id == releaseId,
                s => s.SetProperty(r => r.CreatedDate, DateTimeOffset.UtcNow.AddDays(-ageDays)),
                CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return releaseId;
    }

    private Task SeedDeploymentForReleaseAsync((int ProjectId, int EnvironmentId, int ChannelId) graph, int releaseId, bool withSuccessCompletion)
        => Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);

            var task = await builder.CreateServerTaskAsync("Success").ConfigureAwait(false);
            var deployment = await builder.CreateDeploymentAsync(graph.ProjectId, graph.EnvironmentId, releaseId, task.Id, graph.ChannelId).ConfigureAwait(false);

            if (withSuccessCompletion)
                await builder.CreateDeploymentCompletionAsync(deployment.Id, "Success").ConfigureAwait(false);
        });
}
