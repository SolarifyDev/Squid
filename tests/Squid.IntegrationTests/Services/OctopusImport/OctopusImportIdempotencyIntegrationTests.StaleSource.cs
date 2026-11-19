using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Enums.OctopusImport;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// An older copy of the same Octopus content (a session previewed before a newer import, or an older export file)
/// must never roll newer imported history or release notes back.
/// </summary>
public partial class OctopusImportIdempotencyIntegrationTests
{
    // Session S1 previewed while the task was Executing, S2 imports it finished, then S1 is confirmed.
    [Fact]
    public async Task StaleSession_OlderTaskContent_NeverRollsImportedHistoryBack()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { State = "Queued", CompletedTime = null }));

        var stale = await PreviewAsync(Archive(new HistorySpec { State = "Executing", CompletedTime = null }));
        stale.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);

        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var newer = await SingleAsync<ServerTaskEntity>();
        newer.State.ShouldBe(TaskState.Success);

        await ConfirmAsync(await SeedValidatedSessionAsync(), stale);

        var task = await SingleAsync<ServerTaskEntity>();
        task.State.ShouldBe(TaskState.Success, "an older copy of the task replaced newer imported history");
        task.CompletedTime.ShouldBe(newer.CompletedTime);
        (await CountAsync<DeploymentCompletion>()).ShouldBe(1);
    }

    // An export taken while the task ran, imported after an import of the finished task.
    [Fact]
    public async Task OlderExportFile_AfterNewerImport_NeverRollsImportedHistoryBack()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var newer = await SingleAsync<ServerTaskEntity>();

        await PreviewAndConfirmAsync(Archive(new HistorySpec { State = "Executing", CompletedTime = null }));

        var task = await SingleAsync<ServerTaskEntity>();
        task.State.ShouldBe(TaskState.Success, "an older export replaced newer imported history");
        task.CompletedTime.ShouldBe(newer.CompletedTime);
        (await CountAsync<DeploymentCompletion>()).ShouldBe(1);
    }

    // Session S1 previewed with notes v2, S2 imports v3, then S1 is confirmed.
    [Fact]
    public async Task StaleSession_OlderReleaseNotes_NeverReplaceNewerImportedNotes()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { ReleaseNotes = "v1" }));

        var stale = await PreviewAsync(Archive(new HistorySpec { ReleaseNotes = "v2" }));
        stale.PreviewPlan.Resources.Single(r => r.SourceType == "Release").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);

        await ConfirmSucceededAsync(Archive(new HistorySpec { ReleaseNotes = "v3" }));
        (await SingleAsync<Release>()).ReleaseNote.ShouldBe("v3");

        await ConfirmAsync(await SeedValidatedSessionAsync(), stale);

        (await SingleAsync<Release>()).ReleaseNote.ShouldBe("v3", "a stale session replaced newer imported release notes");
    }

    // A plan previewed while the deployment ran (fewer machines), stored before plans named their basis, confirmed after a newer import of
    // the finished deployment: the importer's own guard keeps the finished deployment row as it is, not only its task (name, json,
    // deployed-by, machines and last-modified untouched).
    [Fact]
    public async Task StaleSessionWithoutBasis_OlderCopy_DeploymentRowByteIdentical()
    {
        await ConfirmSucceededAsync(Archive(new HistorySpec { State = "Queued", CompletedTime = null }));

        var stale = await PreviewAsync(Archive(new HistorySpec { State = "Executing", CompletedTime = null, DeployedToMachineIds = "[]" }));
        stale.PreviewPlan.Resources.Single(r => r.SourceType == "Deployment").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        stale.PreviewPlan.Resources.ForEach(r => r.PriorImportFingerprint = null);

        await ConfirmSucceededAsync(Archive(new HistorySpec()));
        var before = await DeploymentRowAsync();

        var result = await ConfirmAsync(await SeedValidatedSessionAsync(), stale);

        result.State.ShouldBe(OctopusImportSessionState.Succeeded, Describe(result.Result));
        (await DeploymentRowAsync()).ShouldBe(before, "an older copy rewrote the finished deployment row");
        (await SingleAsync<ServerTaskEntity>()).State.ShouldBe(TaskState.Success);
    }

    private Task<string> DeploymentRowAsync()
        => Run<SquidDbContext, string>(db => db.Database.SqlQueryRaw<string>("""SELECT row_to_json(d)::text AS "Value" FROM deployment d""").SingleAsync());
}
