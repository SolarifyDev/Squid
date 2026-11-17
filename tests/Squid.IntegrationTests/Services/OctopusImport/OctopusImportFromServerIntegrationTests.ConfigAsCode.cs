using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.OctopusImport;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;
using static Squid.IntegrationTests.Helpers.OctopusConfigAsCodeResources;
using static Squid.IntegrationTests.Helpers.OctopusProjectSelectionResources;

namespace Squid.IntegrationTests.Services.OctopusImport;

/// <summary>
/// Config-as-Code projects end to end through the mediator, the real-socket stub Octopus and the real database: "Api" (Projects-2) is
/// version-controlled. Nothing of its own is requested, nothing is written for it, a project imported before its conversion is left
/// byte-identical, the other projects import as before, and the picker marks it — from the server and after a reload.
/// </summary>
public partial class OctopusImportFromServerIntegrationTests
{
    [Fact]
    public async Task ServerSource_SpaceWithConfigAsCodeProject_ImportsTheRest_WritesNothingForIt()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects().WithApiAsConfigAsCode();

        var result = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(result);
        octopus.Requests.ShouldNotContain(r => r.PathAndQuery.Contains($"/projects/{ApiProjectId}/") || r.PathAndQuery.Contains($"projects={ApiProjectId}"), "nothing of a Config-as-Code project's own is requested");
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName, WorkerProjectName], ignoreOrder: true);
        (await SourceMapAsync()).ShouldNotContain(r => r.SourceId == ApiProjectId || r.SourceId == ChannelOf(ApiProjectId) || r.SourceId == $"variableset-{ApiProjectId}" || r.SourceId == "Releases-2" || r.SourceId == "Deployments-2");
        (await CountAsync<Deployment>()).ShouldBe(1, "only Projects-1's history");
        result.Fetch.ProjectCount.ShouldBe(2);
        (await StoredCursorAsync(result.SessionId)).Counts.ShouldSatisfyAllConditions(c => c.ConfigAsCodeProjects.ShouldBe(1), c => c.ItemsRejected.ShouldBe(0));
        result.PreviewPlan.Resources.Single(r => r.SourceId == ApiProjectId).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported);
        result.PreviewPlan.Summary.ConfigAsCodeProjectCount.ShouldBe(1);
    }

    // Whole space (D4, unchanged): what only the Config-as-Code project uses is imported like any resource no project uses.
    [Fact]
    public async Task ServerSource_WholeSpace_WhatOnlyTheConfigAsCodeProjectUsesFollowsTheWholeSpaceRule()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects().WithApiAsConfigAsCode();

        ShouldHaveSucceeded(await ImportFromServerAsync(octopus));

        (await NamesAsync<Lifecycle>(l => l.Name)).ShouldContain(ApiLifecycleName);
    }

    // With a selection that names it, what only it uses is "not used by the chosen projects".
    [Fact]
    public async Task ServerSource_ChosenConfigAsCodeProject_SkippedWithItsReason_WhatOnlyItUsesNotImported()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects().WithApiAsConfigAsCode();

        var result = await ImportChosenAsync(octopus, ["Projects-1", ApiProjectId]);

        ShouldHaveSucceeded(result);
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName]);
        (await NamesAsync<Lifecycle>(l => l.Name)).ShouldNotContain(ApiLifecycleName);
        (await NamesAsync<LibraryVariableSet>(l => l.Name)).ShouldNotContain(ApiLibraryName);
        result.PreviewPlan.Resources.Single(r => r.SourceId == ApiProjectId).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported);
        result.PreviewPlan.Resources.Single(r => r.SourceId == "LibraryVariableSets-2").SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.NotUsedByChosenProjects);
    }

    // Imported, then converted in Octopus: the re-import leaves every row of it, and every source-map row of it, exactly as it was.
    [Fact]
    public async Task ServerSource_ProjectImportedThenConvertedToConfigAsCode_ReimportLeavesItByteIdentical()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects();
        var first = await ImportFromServerAsync(octopus);
        ShouldHaveSucceeded(first);
        var apiSourceIds = first.PreviewPlan.Resources.Where(r => r.SourceId == ApiProjectId || r.OwnerProjectSourceId == ApiProjectId).Select(r => r.SourceId).ToHashSet();
        var before = await ApiSnapshotAsync(apiSourceIds);

        octopus.WithApiAsConfigAsCode();
        var second = await ImportFromServerAsync(octopus);

        ShouldHaveSucceeded(second);
        (await ApiSnapshotAsync(apiSourceIds)).ShouldBe(before);
        second.PreviewPlan.Resources.Where(r => r.SourceId == ApiProjectId || r.OwnerProjectSourceId == ApiProjectId).ShouldAllBe(r => r.PreviewAction == OctopusImportPreviewAction.Skip);
    }

    [Fact]
    public async Task ServerAndSessionListing_MarkTheConfigAsCodeProject()
    {
        await using var octopus = OneDeployment().WithTwoMoreProjects().WithApiAsConfigAsCode();

        var server = (await Run<IMediator, Squid.Message.Requests.OctopusImport.ListOctopusServerProjectsResponse>(mediator => OctopusImportHandlerFlow.ListServerProjectsAsync(mediator, SpaceId, octopus.BaseUri, ApiKey))).Data;
        var session = await Run<IMediator, OctopusImportProjectListDto>(async mediator => (await OctopusImportHandlerFlow.ListSessionProjectsAsync(mediator, SpaceId, await ConnectChooseAndReadAsync(mediator, octopus, ["Projects-1", ApiProjectId]))).Data);

        server.Projects.Where(p => p.IsConfigAsCode).Select(p => p.Id).ShouldBe([ApiProjectId]);
        session.Projects.Where(p => p.IsConfigAsCode).Select(p => p.Id).ShouldBe([ApiProjectId]);
    }

    // The export file: a Config-as-Code project document without a process is skipped, never blocking; the others are created.
    [Fact]
    public async Task ExportFile_ConfigAsCodeProjectDocument_Skipped_TheRestImported()
    {
        var result = await ImportFromFileAsync(ExportArchive(extra: ConfigAsCodeApiDocuments()));

        ShouldHaveSucceeded(result);
        (await NamesAsync<Project>(p => p.Name)).ShouldBe([ServerProjectName, WorkerProjectName], ignoreOrder: true);
        result.PreviewPlan.Resources.Single(r => r.SourceId == ApiProjectId).SkipReason.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ConfigAsCodeNotSupported);
        (await SourceMapAsync()).ShouldNotContain(r => r.SourceId == ApiProjectId);
    }

    // Every Squid row the Api project owns, and every source-map row of it, field for field.
    private Task<List<string>> ApiSnapshotAsync(IReadOnlySet<string> apiSourceIds)
        => Run<SquidDbContext, List<string>>(async db =>
        {
            var project = await db.Set<Project>().AsNoTracking().SingleAsync(p => p.Name == ApiProjectName);
            var rows = new List<object> { project };

            rows.AddRange(await db.Set<Channel>().AsNoTracking().Where(c => c.ProjectId == project.Id).OrderBy(c => c.Id).ToListAsync());
            rows.AddRange(await db.Set<DeploymentProcess>().AsNoTracking().Where(p => p.ProjectId == project.Id).OrderBy(p => p.Id).ToListAsync());
            rows.AddRange(await db.Set<VariableSet>().AsNoTracking().Where(v => v.Id == project.VariableSetId).ToListAsync());
            rows.AddRange(await db.Set<Variable>().AsNoTracking().Where(v => v.VariableSetId == project.VariableSetId).OrderBy(v => v.Id).ToListAsync());
            rows.AddRange(await db.Set<Release>().AsNoTracking().Where(r => r.ProjectId == project.Id).OrderBy(r => r.Id).ToListAsync());
            rows.AddRange((await db.Set<OctopusImportSourceMap>().AsNoTracking().ToListAsync()).Where(r => apiSourceIds.Contains(r.SourceId)).OrderBy(r => r.Id));

            return rows.Select(r => $"{r.GetType().Name}:{JsonSerializer.Serialize(r, r.GetType())}").ToList();
        });
}
