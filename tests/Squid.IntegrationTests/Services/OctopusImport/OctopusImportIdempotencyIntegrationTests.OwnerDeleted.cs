using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.OctopusImport;
using EnvironmentEntity = Squid.Core.Persistence.Entities.Deployments.Environment;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Squid deletes of a release, channel or environment do not cascade: the imported deployment, its task and its completion stay
/// behind. A re-import re-creates the deleted owner and moves that history onto it; it never imports the Octopus deployment twice.
/// </summary>
public partial class OctopusImportIdempotencyIntegrationTests
{
    [Theory]
    [InlineData("release")]
    [InlineData("channel")]
    [InlineData("environment")]
    public async Task OwnerDeletedInSquid_Reimport_MovesTheHistoryOntoTheRecreatedOwner_NeverImportsItTwice(string ownerTable)
    {
        var archive = Archive(new HistorySpec());
        await ConfirmSucceededAsync(archive);
        var imported = await SingleAsync<Deployment>();
        await DeleteOwnerRowAsync(ownerTable, imported);

        // A channel deleted in Squid stays deleted unless the user ticks it: ticked, it is created again and takes its history back.
        var (_, result) = await PreviewAndConfirmAsync(archive, choices: ownerTable == "channel" ? new Dictionary<string, bool> { ["Channels-1"] = true } : null);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await CountAsync<Release>()).ShouldBe(1, "the release was imported a second time");
        await AssertSingleHistoryUnitAsync();
        await AssertHistoryPointsAtLiveOwnersAsync(imported.Id);
    }

    // The relink still never rolls history back: an older export (task still running) re-imported after the delete moves the
    // deployment onto the re-created release, and the finished task Squid holds is kept.
    [Fact]
    public async Task ReleaseDeletedInSquid_OlderExportReimported_HistoryMovedNeverRolledBack()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var imported = await SingleAsync<Deployment>();
        var finished = await SingleAsync<ServerTaskEntity>();
        await DeleteOwnerRowAsync("release", imported);

        var (_, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec { State = "Executing", CompletedTime = null }));

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        await AssertSingleHistoryUnitAsync();
        await AssertHistoryPointsAtLiveOwnersAsync(imported.Id);
        var task = await SingleAsync<ServerTaskEntity>();
        task.State.ShouldBe(TaskState.Success, "an older copy replaced newer imported history");
        task.CompletedTime.ShouldBe(finished.CompletedTime);
    }

    // Octopus moved the imported release onto another (imported) channel and edited its notes. Nothing was deleted in Squid, so the
    // release is never moved: not onto a channel holding a hand-made release of that version (which would break the import), and not
    // silently onto the other channel either. Only the notes are updated.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReleaseMovedToAnotherChannelInOctopus_NothingDeleted_StaysPut_NotesUpdated(bool handMadeReleaseOnTarget)
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { MovedDefaultChannel = true }));
        var imported = await Run<SquidDbContext, Release>(db => db.Set<Release>().AsNoTracking().SingleAsync(r => r.Version == "1.0.0"));
        var target = await Run<SquidDbContext, Channel>(db => db.Set<Channel>().AsNoTracking().SingleAsync(c => c.Name == "Hotfix"));

        if (handMadeReleaseOnTarget) await AddReleaseInSquidAsync(target, "1.0.0");

        var (_, result) = await PreviewAndConfirmAsync(Archive(new HistorySpec { MovedDefaultChannel = true, ReleaseChannelId = "Channels-9", ReleaseNotes = "edited in Octopus" }));

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        var after = await Run<SquidDbContext, Release>(db => db.Set<Release>().AsNoTracking().SingleAsync(r => r.Id == imported.Id));
        (after.ProjectId, after.ChannelId).ShouldBe((imported.ProjectId, imported.ChannelId), "a release whose owners exist was moved");
        after.ReleaseNote.ShouldBe("edited in Octopus");
        (await Run<SquidDbContext, int>(db => db.Set<Release>().CountAsync(r => r.Version == "1.0.0" && r.ChannelId == target.Id))).ShouldBe(handMadeReleaseOnTarget ? 1 : 0);
    }

    private Task AddReleaseInSquidAsync(Channel channel, string version)
        => Run<SquidDbContext>(async db =>
        {
            db.Set<Release>().Add(new Release { Version = version, ProjectId = channel.ProjectId, ChannelId = channel.Id, SpaceId = channel.SpaceId, ReleaseNote = "made in Squid", CreatedDate = DateTimeOffset.UtcNow, LastModifiedDate = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        });

    // The same single-row delete the Squid services perform: no cascade to what the row owned.
    private Task DeleteOwnerRowAsync(string ownerTable, Deployment deployment)
    {
        var ownerId = ownerTable switch
        {
            "release" => deployment.ReleaseId,
            "channel" => deployment.ChannelId,
            _ => deployment.EnvironmentId
        };

        return Run<SquidDbContext>(db => db.Database.ExecuteSqlRawAsync($"DELETE FROM {ownerTable} WHERE id = {{0}}", ownerId));
    }

    private async Task AssertHistoryPointsAtLiveOwnersAsync(int deploymentId)
    {
        var deployment = await SingleAsync<Deployment>();
        var release = await SingleAsync<Release>();
        var task = await SingleAsync<ServerTaskEntity>();

        deployment.Id.ShouldBe(deploymentId, "the deployment Squid still holds is kept, not imported again");
        (deployment.ReleaseId, deployment.ChannelId).ShouldBe((release.Id, release.ChannelId));
        (await ListAsync<Channel>()).Select(c => c.Id).ShouldContain(release.ChannelId);
        (await ListAsync<EnvironmentEntity>()).Select(e => e.Id).ShouldContain(deployment.EnvironmentId);
        (task.ProjectId, task.EnvironmentId).ShouldBe((deployment.ProjectId, deployment.EnvironmentId));
    }

    private async Task AssertSingleHistoryUnitAsync()
    {
        (await CountAsync<Deployment>()).ShouldBe(1, "the Octopus deployment was imported a second time");
        (await CountAsync<ServerTaskEntity>()).ShouldBe(1, "the Octopus task was imported a second time");
        (await CountAsync<DeploymentCompletion>()).ShouldBe(1, "the completion was written a second time");
        (await EventsAsync()).Count.ShouldBe(1, "the history events were written a second time");
    }
}
