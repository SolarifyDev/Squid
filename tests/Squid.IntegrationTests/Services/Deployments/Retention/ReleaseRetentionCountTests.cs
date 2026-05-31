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
using TaskState = Squid.Core.Services.Deployments.ServerTask.TaskState;

namespace Squid.IntegrationTests.Services.Deployments.Retention;

/// <summary>
/// Integration coverage (real Postgres) for count-based release retention (the <c>Items</c> unit):
/// when a lifecycle keeps a limited number of releases, <c>RetentionPolicyEnforcer</c> keeps the
/// newest N releases per channel and cascade-deletes the rest — each pruned release takes its
/// deployments + task data (ServerTask / ActivityLog / ServerTaskLog / interruptions / checkpoints)
/// + completions + ReleaseSelectedPackage rows with it, then its process/variable snapshots are
/// ref-count-GC'd (a snapshot still referenced by a surviving release is kept). Releases that are
/// currently deployed or have an in-progress deployment are always preserved, even beyond N.
/// KeepForever (the lifecycle default) and a non-positive keep count both prune nothing, so the
/// feature is opt-in / non-breaking. Each test seeds its own isolated project graph.
/// </summary>
public class ReleaseRetentionCountTests : TestBase
{
    public ReleaseRetentionCountTests() : base("ReleaseRetentionCount", "squid_it_release_retention_count")
    {
    }

    [Fact]
    public async Task BeyondKeepCount_OldestReleasesPruned_WithSnapshots()
    {
        var graph = await SeedGraphAsync(keepCount: 2).ConfigureAwait(false);

        var newest = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var second = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 2).ConfigureAwait(false);
        var third = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 3).ConfigureAwait(false);
        var oldest = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 4).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(newest.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Newest release must be kept.");
        (await ReleaseExistsAsync(second.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Second-newest must be kept (within keep=2).");
        (await ReleaseExistsAsync(third.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Third release is beyond keep=2 and must be pruned.");
        (await ReleaseExistsAsync(oldest.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Oldest release is beyond keep=2 and must be pruned.");

        (await PackageCountAsync(third.ReleaseId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Pruned release's packages must cascade.");
        (await ProcessSnapshotExistsAsync(third.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Orphaned process snapshot must be GC'd.");
        (await VariableSnapshotExistsAsync(oldest.VariableSnapshotId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Orphaned variable snapshot must be GC'd.");

        (await ProcessSnapshotExistsAsync(newest.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Surviving release's snapshot must be kept.");
    }

    [Fact]
    public async Task BeyondKeepCount_WithDeployment_CascadesDeploymentTaskAndCompletion()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);

        var kept = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var pruned = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 5).ConfigureAwait(false);

        // The pruned release has a finished (terminal, non-success) deployment, so it is neither
        // currently deployed nor in-flight — the count cascade must take the whole deployment graph.
        var (deploymentId, taskId) = await SeedDeploymentAsync(graph, pruned.ReleaseId, graph.ChannelId, ageDays: 5, TaskState.Failed, completionState: TaskState.Failed).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(kept.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Newest release must be kept.");
        (await ReleaseExistsAsync(pruned.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Beyond-keep release must be pruned even though it had a deployment.");

        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "The pruned release's deployment must cascade.");
        (await TaskExistsAsync(taskId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "The deployment's ServerTask must cascade.");
        (await ActivityCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Activity-log tree must cascade with the task.");
        (await LogCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Task-log lines must cascade with the task.");
        (await InterruptionCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Interruptions must cascade with the task.");
        (await CheckpointCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Checkpoints must cascade with the task.");
        (await CompletionCountAsync(deploymentId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Deployment completions must cascade with the deployment.");
        (await PackageCountAsync(pruned.ReleaseId).ConfigureAwait(false)).ShouldBe(0, customMessage: "Packages must cascade with the release.");
        (await ProcessSnapshotExistsAsync(pruned.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Orphaned snapshot must be GC'd.");

        (await ReleaseExistsAsync(kept.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ProcessSnapshotExistsAsync(kept.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task CurrentlyDeployedRelease_BeyondKeepCount_Kept()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);

        var newest = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var middle = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 5).ConfigureAwait(false);
        var deployedOld = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 9).ConfigureAwait(false);

        var (deploymentId, _) = await SeedDeploymentAsync(graph, deployedOld.ReleaseId, graph.ChannelId, ageDays: 9, TaskState.Success, completionState: TaskState.Success).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(newest.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Newest release kept by count.");
        (await ReleaseExistsAsync(middle.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Middle release is beyond keep=1 and not preserved → pruned.");
        (await ReleaseExistsAsync(deployedOld.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "A currently-deployed release must be preserved even beyond the keep count.");
        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "The preserved release's deployment must survive.");
    }

    [Fact]
    public async Task ReleaseWithInProgressDeployment_BeyondKeepCount_Kept()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);

        var newest = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var inFlightOld = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 9).ConfigureAwait(false);

        // Non-terminal task (Executing) with no success completion: not currently deployed, but
        // mid-flight — pruning it would yank the release out from under a running deployment.
        var (deploymentId, _) = await SeedDeploymentAsync(graph, inFlightOld.ReleaseId, graph.ChannelId, ageDays: 9, TaskState.Executing, completionState: null).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(newest.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(inFlightOld.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "A release with an in-progress deployment must be preserved even beyond the keep count.");
        (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task PerChannel_KeepNewestNIndependently()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);
        var otherChannelId = await SeedExtraChannelAsync(graph).ConfigureAwait(false);

        var aNew = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var aOld = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 3).ConfigureAwait(false);
        var bNew = await SeedReleaseWithOwnSnapshotsAsync(graph, otherChannelId, ageDays: 1).ConfigureAwait(false);
        var bOld = await SeedReleaseWithOwnSnapshotsAsync(graph, otherChannelId, ageDays: 3).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(aNew.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Channel A newest kept.");
        (await ReleaseExistsAsync(aOld.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Channel A older pruned.");
        (await ReleaseExistsAsync(bNew.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "Channel B newest kept independently of channel A.");
        (await ReleaseExistsAsync(bOld.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Channel B older pruned.");
    }

    [Fact]
    public async Task SharedSnapshot_KeptWhileSurvivingReleaseReferencesIt()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);

        // Two releases share the SAME snapshots. The newest is kept (by count), the older is pruned —
        // so the shared snapshots must survive (ref-count protects them).
        var kept = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 1, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);
        var pruned = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 5, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(kept).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(pruned).ConfigureAwait(false)).ShouldBeFalse();
        (await ProcessSnapshotExistsAsync(snap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "A snapshot still referenced by a surviving release must NOT be GC'd.");
        (await VariableSnapshotExistsAsync(snap.VariableSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task KeepForeverLifecycle_PrunesNothing()
    {
        var graph = await SeedGraphAsync(keepCount: 1, keepForever: true).ConfigureAwait(false);

        var a = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 10).ConfigureAwait(false);
        var b = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 20).ConfigureAwait(false);
        var c = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 30).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(a.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(b.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(c.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "KeepForever (the lifecycle default) must prune nothing even when the unit is Items.");
    }

    [Fact]
    public async Task KeepCountZero_PrunesNothing()
    {
        var graph = await SeedGraphAsync(keepCount: 0).ConfigureAwait(false);

        var a = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 10).ConfigureAwait(false);
        var b = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 20).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(a.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(
            customMessage: "A non-positive keep count is treated as keep-forever (no-op), never 'delete everything'.");
        (await ReleaseExistsAsync(b.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task BeyondKeepCount_ReleaseWithMultipleDeployments_AllDeploymentsCascade()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);

        var kept = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var pruned = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 5).ConfigureAwait(false);

        // The pruned release was deployed twice (e.g. two environments / re-deploys); both finished
        // unsuccessfully, so it is neither currently deployed nor in-flight. Every deployment + its
        // task data must cascade.
        var d1 = await SeedDeploymentAsync(graph, pruned.ReleaseId, graph.ChannelId, ageDays: 5, TaskState.Failed, completionState: TaskState.Failed).ConfigureAwait(false);
        var d2 = await SeedDeploymentAsync(graph, pruned.ReleaseId, graph.ChannelId, ageDays: 6, TaskState.Failed, completionState: TaskState.Failed).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(pruned.ReleaseId).ConfigureAwait(false)).ShouldBeFalse();

        foreach (var (deploymentId, taskId) in new[] { d1, d2 })
        {
            (await DeploymentExistsAsync(deploymentId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Every deployment of the pruned release must cascade.");
            (await TaskExistsAsync(taskId).ConfigureAwait(false)).ShouldBeFalse();
            (await LogCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0);
            (await InterruptionCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0);
            (await CheckpointCountAsync(taskId).ConfigureAwait(false)).ShouldBe(0);
        }

        (await ReleaseExistsAsync(kept.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task EnforceRunTwice_Idempotent_KeepsSurvivorsPrunesOnce()
    {
        var graph = await SeedGraphAsync(keepCount: 2).ConfigureAwait(false);

        var r1 = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var r2 = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 2).ConfigureAwait(false);
        var r3 = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 3).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);
        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);   // second run must be a clean no-op

        (await ReleaseExistsAsync(r1.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(r2.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await PackageCountAsync(r1.ReleaseId).ConfigureAwait(false)).ShouldBe(1, customMessage: "Surviving release's package must remain intact across repeated runs.");
        (await PackageCountAsync(r2.ReleaseId).ConfigureAwait(false)).ShouldBe(1);
        (await ProcessSnapshotExistsAsync(r1.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
        (await ProcessSnapshotExistsAsync(r2.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();

        (await ReleaseExistsAsync(r3.ReleaseId).ConfigureAwait(false)).ShouldBeFalse();
        (await ProcessSnapshotExistsAsync(r3.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task PruningOneChannel_LeavesOtherChannelFullyIntact()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);
        var otherChannelId = await SeedExtraChannelAsync(graph).ConfigureAwait(false);

        var aNew = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var aOld = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 5).ConfigureAwait(false);

        // Other channel: a single old release with a full deployment graph. As the only (hence newest)
        // release in its channel it stays within keep=1, so NOTHING of it may be touched.
        var bOnly = await SeedReleaseWithOwnSnapshotsAsync(graph, otherChannelId, ageDays: 9).ConfigureAwait(false);
        var (bDeployment, bTask) = await SeedDeploymentAsync(graph, bOnly.ReleaseId, otherChannelId, ageDays: 9, TaskState.Failed, completionState: TaskState.Failed).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(aNew.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(aOld.ReleaseId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "Channel A's oldest is beyond keep=1 → pruned.");

        (await ReleaseExistsAsync(bOnly.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "The other channel's release must be left fully intact.");
        (await PackageCountAsync(bOnly.ReleaseId).ConfigureAwait(false)).ShouldBe(1);
        (await DeploymentExistsAsync(bDeployment).ConfigureAwait(false)).ShouldBeTrue(customMessage: "The other channel's deployment must not be cascaded.");
        (await TaskExistsAsync(bTask).ConfigureAwait(false)).ShouldBeTrue();
        (await LogCountAsync(bTask).ConfigureAwait(false)).ShouldBe(1);
        (await ProcessSnapshotExistsAsync(bOnly.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task MixedRetentionBatch_PreservedAndWithinCountKept_RestPrunedWithCascade()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);

        var newest = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);       // within keep=1
        var deployedOld = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 4).ConfigureAwait(false);  // currently deployed
        var activeOld = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 6).ConfigureAwait(false);    // in-progress
        var prunedWithDep = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 8).ConfigureAwait(false); // pruned + cascade
        var prunedBare = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 10).ConfigureAwait(false);   // pruned (no deployment)

        var (depKeep, _) = await SeedDeploymentAsync(graph, deployedOld.ReleaseId, graph.ChannelId, ageDays: 4, TaskState.Success, completionState: TaskState.Success).ConfigureAwait(false);
        var (depActive, _) = await SeedDeploymentAsync(graph, activeOld.ReleaseId, graph.ChannelId, ageDays: 6, TaskState.Executing, completionState: null).ConfigureAwait(false);
        var (depPruned, taskPruned) = await SeedDeploymentAsync(graph, prunedWithDep.ReleaseId, graph.ChannelId, ageDays: 8, TaskState.Failed, completionState: TaskState.Failed).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(newest.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "newest is within keep=1");
        (await ReleaseExistsAsync(deployedOld.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "currently-deployed release preserved beyond count");
        (await ReleaseExistsAsync(activeOld.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "in-progress release preserved beyond count");
        (await DeploymentExistsAsync(depKeep).ConfigureAwait(false)).ShouldBeTrue();
        (await DeploymentExistsAsync(depActive).ConfigureAwait(false)).ShouldBeTrue();

        (await ReleaseExistsAsync(prunedWithDep.ReleaseId).ConfigureAwait(false)).ShouldBeFalse();
        (await DeploymentExistsAsync(depPruned).ConfigureAwait(false)).ShouldBeFalse();
        (await TaskExistsAsync(taskPruned).ConfigureAwait(false)).ShouldBeFalse();
        (await ReleaseExistsAsync(prunedBare.ReleaseId).ConfigureAwait(false)).ShouldBeFalse();
        (await ProcessSnapshotExistsAsync(prunedBare.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeFalse();
    }

    [Fact]
    public async Task KeepCountEqualsReleaseCount_PrunesNothing()
    {
        var graph = await SeedGraphAsync(keepCount: 3).ConfigureAwait(false);

        var r1 = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 1).ConfigureAwait(false);
        var r2 = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 5).ConfigureAwait(false);
        var r3 = await SeedReleaseWithOwnSnapshotsAsync(graph, graph.ChannelId, ageDays: 9).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(r1.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(r2.ReleaseId).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(r3.ReleaseId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "keepCount == release count → nothing exceeds the window.");
    }

    [Fact]
    public async Task TwoPrunedReleasesSharingSnapshot_SnapshotGarbageCollectedOnce()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);
        var keptSnap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var sharedSnap = await SeedSnapshotPairAsync().ConfigureAwait(false);

        var kept = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 1, keptSnap.ProcessSnapshotId, keptSnap.VariableSnapshotId).ConfigureAwait(false);
        // Two old releases share the SAME snapshot pair; both are beyond keep=1 and unpreserved, so the
        // snapshot becomes truly orphaned and must be GC'd exactly once (the id appears twice → dedup).
        var prunedA = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 5, sharedSnap.ProcessSnapshotId, sharedSnap.VariableSnapshotId).ConfigureAwait(false);
        var prunedB = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 7, sharedSnap.ProcessSnapshotId, sharedSnap.VariableSnapshotId).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(kept).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(prunedA).ConfigureAwait(false)).ShouldBeFalse();
        (await ReleaseExistsAsync(prunedB).ConfigureAwait(false)).ShouldBeFalse();
        (await ProcessSnapshotExistsAsync(sharedSnap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeFalse(customMessage: "A snapshot orphaned by pruning ALL its referencing releases must be GC'd.");
        (await VariableSnapshotExistsAsync(sharedSnap.VariableSnapshotId).ConfigureAwait(false)).ShouldBeFalse();
        (await ProcessSnapshotExistsAsync(keptSnap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    [Fact]
    public async Task PrunedReleaseSnapshot_KeptWhenReferencedBySurvivingDeployment()
    {
        var graph = await SeedGraphAsync(keepCount: 1).ConfigureAwait(false);
        var keptSnap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var sharedSnap = await SeedSnapshotPairAsync().ConfigureAwait(false);

        // The kept (newest) release references its OWN snapshot, but carries a surviving deployment that
        // references the SHARED snapshot. The pruned (old) release also references the shared snapshot.
        // After pruning, no surviving RELEASE references the shared snapshot — only the surviving
        // deployment does — so ref-count GC must keep it (never delete a snapshot still in use).
        var kept = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 1, keptSnap.ProcessSnapshotId, keptSnap.VariableSnapshotId).ConfigureAwait(false);
        await SeedDeploymentReferencingSnapshotsAsync(graph, kept, graph.ChannelId, sharedSnap.ProcessSnapshotId, sharedSnap.VariableSnapshotId).ConfigureAwait(false);
        var pruned = await SeedReleaseAsync(graph.ProjectId, graph.ChannelId, ageDays: 5, sharedSnap.ProcessSnapshotId, sharedSnap.VariableSnapshotId).ConfigureAwait(false);

        await EnforceAsync(graph.ProjectId).ConfigureAwait(false);

        (await ReleaseExistsAsync(kept).ConfigureAwait(false)).ShouldBeTrue();
        (await ReleaseExistsAsync(pruned).ConfigureAwait(false)).ShouldBeFalse();
        (await ProcessSnapshotExistsAsync(sharedSnap.ProcessSnapshotId).ConfigureAwait(false)).ShouldBeTrue(customMessage: "A snapshot still referenced by a surviving deployment must NOT be GC'd.");
        (await VariableSnapshotExistsAsync(sharedSnap.VariableSnapshotId).ConfigureAwait(false)).ShouldBeTrue();
    }

    // ── enforce / assertion helpers ──

    private Task EnforceAsync(int projectId)
        => Run<IRetentionPolicyEnforcer>(enforcer => enforcer.EnforceRetentionForProjectAsync(projectId, CancellationToken.None));

    private Task<bool> ReleaseExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<Release>(r => r.Id == id, CancellationToken.None));

    private Task<bool> DeploymentExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<Deployment>(d => d.Id == id, CancellationToken.None));

    private Task<bool> TaskExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<ServerTask>(t => t.Id == id, CancellationToken.None));

    private Task<int> PackageCountAsync(int releaseId)
        => Run<IRepository, int>(repo => repo.CountAsync<ReleaseSelectedPackage>(p => p.ReleaseId == releaseId, CancellationToken.None));

    private Task<int> CompletionCountAsync(int deploymentId)
        => Run<IRepository, int>(repo => repo.CountAsync<DeploymentCompletion>(c => c.DeploymentId == deploymentId, CancellationToken.None));

    private Task<int> ActivityCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<ActivityLog>(a => a.ServerTaskId == taskId, CancellationToken.None));

    private Task<int> LogCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<ServerTaskLog>(l => l.ServerTaskId == taskId, CancellationToken.None));

    private Task<int> InterruptionCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<DeploymentInterruption>(i => i.ServerTaskId == taskId, CancellationToken.None));

    private Task<int> CheckpointCountAsync(int taskId)
        => Run<IRepository, int>(repo => repo.CountAsync<DeploymentExecutionCheckpoint>(c => c.ServerTaskId == taskId, CancellationToken.None));

    private Task<bool> ProcessSnapshotExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<DeploymentProcessSnapshot>(s => s.Id == id, CancellationToken.None));

    private Task<bool> VariableSnapshotExistsAsync(int id)
        => Run<IRepository, bool>(repo => repo.AnyAsync<VariableSetSnapshot>(s => s.Id == id, CancellationToken.None));

    // ── seeding helpers ──

    private async Task<(int ProjectId, int EnvironmentId, int ChannelId, int LifecycleId)> SeedGraphAsync(int keepCount, bool keepForever = false)
    {
        var graph = (ProjectId: 0, EnvironmentId: 0, ChannelId: 0, LifecycleId: 0);

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
                ReleaseRetentionUnit = RetentionPolicyUnit.Items,
                ReleaseRetentionQuantity = keepCount,
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

            graph = (project.Id, environment.Id, channel.Id, lifecycle.Id);
        }).ConfigureAwait(false);

        return graph;
    }

    private async Task<int> SeedExtraChannelAsync((int ProjectId, int EnvironmentId, int ChannelId, int LifecycleId) graph)
    {
        var channelId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);
            var channel = await builder.CreateChannelAsync(graph.ProjectId, graph.LifecycleId, $"channel-{Guid.NewGuid():N}").ConfigureAwait(false);
            channelId = channel.Id;
        }).ConfigureAwait(false);

        return channelId;
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

    private async Task<(int ReleaseId, int ProcessSnapshotId, int VariableSnapshotId)> SeedReleaseWithOwnSnapshotsAsync((int ProjectId, int EnvironmentId, int ChannelId, int LifecycleId) graph, int channelId, int ageDays)
    {
        var snap = await SeedSnapshotPairAsync().ConfigureAwait(false);
        var releaseId = await SeedReleaseAsync(graph.ProjectId, channelId, ageDays, snap.ProcessSnapshotId, snap.VariableSnapshotId).ConfigureAwait(false);

        return (releaseId, snap.ProcessSnapshotId, snap.VariableSnapshotId);
    }

    private async Task<int> SeedReleaseAsync(int projectId, int channelId, int ageDays, int processSnapshotId, int variableSnapshotId, bool withPackage = true)
    {
        var releaseId = 0;

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var release = new Release
            {
                Version = $"1.0.{Guid.NewGuid():N}",
                ProjectId = projectId,
                ChannelId = channelId,
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

    private async Task<(int DeploymentId, int TaskId)> SeedDeploymentAsync((int ProjectId, int EnvironmentId, int ChannelId, int LifecycleId) graph, int releaseId, int channelId, int ageDays, string taskState, string? completionState)
    {
        var result = (DeploymentId: 0, TaskId: 0);

        await Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);

            var task = await builder.CreateServerTaskAsync(taskState).ConfigureAwait(false);

            await repo.InsertAsync(new ActivityLog
            {
                ServerTaskId = task.Id,
                Name = "root",
                NodeType = DeploymentActivityLogNodeType.Task,
                Category = DeploymentActivityLogCategory.Info,
                Status = DeploymentActivityLogNodeStatus.Success,
                StartedAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
                SortOrder = 0,
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await repo.InsertAsync(new ServerTaskLog
            {
                ServerTaskId = task.Id,
                Category = ServerTaskLogCategory.Info,
                MessageText = "deployment log line",
                Source = "test",
                OccurredAt = DateTimeOffset.UtcNow.AddDays(-ageDays),
                SequenceNumber = 1,
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var deployment = await builder.CreateDeploymentAsync(graph.ProjectId, graph.EnvironmentId, releaseId, task.Id, channelId).ConfigureAwait(false);

            await repo.InsertAsync(new DeploymentInterruption
            {
                ServerTaskId = task.Id,
                DeploymentId = deployment.Id,
                StepName = "step",
                ActionName = "action",
                MachineName = "machine",
                ErrorMessage = string.Empty,
                FormJson = "{}",
                SubmittedValuesJson = "{}",
                ResponsibleUserId = string.Empty,
                ResponsibleTeamIds = string.Empty,
                Resolution = string.Empty,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await repo.InsertAsync(new DeploymentExecutionCheckpoint
            {
                ServerTaskId = task.Id,
                DeploymentId = deployment.Id,
                LastCompletedBatchIndex = 0,
                FailureEncountered = false,
                OutputVariablesJson = "{}",
                LastModifiedDate = DateTimeOffset.UtcNow
            }, CancellationToken.None).ConfigureAwait(false);

            await uow.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            await repo.ExecuteUpdateAsync<Deployment>(
                d => d.Id == deployment.Id,
                s => s.SetProperty(d => d.CreatedDate, DateTimeOffset.UtcNow.AddDays(-ageDays)),
                CancellationToken.None).ConfigureAwait(false);

            if (completionState != null)
                await builder.CreateDeploymentCompletionAsync(deployment.Id, completionState).ConfigureAwait(false);

            result = (deployment.Id, task.Id);
        }).ConfigureAwait(false);

        return result;
    }

    // Seeds a surviving (terminal, no success completion) deployment on an existing release and points
    // its snapshot ids at a specific pair — used to prove ref-count GC keeps a snapshot still in use.
    private Task SeedDeploymentReferencingSnapshotsAsync((int ProjectId, int EnvironmentId, int ChannelId, int LifecycleId) graph, int releaseId, int channelId, int processSnapshotId, int variableSnapshotId)
        => Run<IRepository, IUnitOfWork>(async (repo, uow) =>
        {
            var builder = new TestDataBuilder(repo, uow);

            var task = await builder.CreateServerTaskAsync("Failed").ConfigureAwait(false);
            var deployment = await builder.CreateDeploymentAsync(graph.ProjectId, graph.EnvironmentId, releaseId, task.Id, channelId).ConfigureAwait(false);

            await repo.ExecuteUpdateAsync<Deployment>(
                d => d.Id == deployment.Id,
                s => s.SetProperty(d => d.ProcessSnapshotId, processSnapshotId).SetProperty(d => d.VariableSetSnapshotId, variableSnapshotId),
                CancellationToken.None).ConfigureAwait(false);
        });
}
