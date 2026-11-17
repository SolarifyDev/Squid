using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportServerOwnedRowsTests
{
    // History rows and every row the user decides about (and its followers) are the server's; the summary too. Other rows stay the client's.
    [Fact]
    public void Merge_ChoiceRowsFollowersHistoryAndSummaryFromTheServer_OtherRowsFromTheClient()
    {
        var server = Plan(Row("Projects-1", "Project", OctopusImportPreviewAction.AlreadyImported), Choice("Steps-2", OctopusImportPreviewAction.Create, null), Choice("Actions-2", OctopusImportPreviewAction.Create, "Steps-2"), Choice("Phases-9", OctopusImportPreviewAction.Skip, null), Row("Releases-1", "Release", OctopusImportPreviewAction.Update));
        server.Summary = new OctopusImportPreviewSummaryDto { IsUpToDate = false, Choices = [new OctopusImportChoiceCountDto { Total = 2, Selected = 1 }] };
        var client = Plan(Row("Projects-1", "Project", OctopusImportPreviewAction.ReuseExisting), Choice("Steps-2", OctopusImportPreviewAction.Skip, null), Row("Actions-2", "DeploymentAction", OctopusImportPreviewAction.Skip), Row("Releases-1", "Release", OctopusImportPreviewAction.Create));
        client.Summary = new OctopusImportPreviewSummaryDto { IsUpToDate = true };

        var merged = OctopusImportServerOwnedRows.MergeServerOwnedRows(client, server);

        merged.Resources.Select(r => $"{r.SourceId}|{r.PreviewAction}|{r.Choice?.FollowsSourceId}").ShouldBe(["Projects-1|ReuseExisting|", "Steps-2|Create|", "Actions-2|Create|Steps-2", "Phases-9|Skip|", "Releases-1|Update|"]);
        merged.Summary.ShouldBeSameAs(server.Summary);
    }

    // A client row claiming a choice the server does not offer is kept as posted: validation refuses it, merging never hides it.
    [Fact]
    public void Merge_ForgedClientChoiceRow_KeptForValidation()
    {
        var server = Plan(Row("Steps-1", "DeploymentStep", OctopusImportPreviewAction.AlreadyImported));
        var client = Plan(Choice("Steps-1", OctopusImportPreviewAction.Create, null));

        var merged = OctopusImportServerOwnedRows.MergeServerOwnedRows(client, server);

        merged.Resources.Single().ShouldSatisfyAllConditions(r => r.PreviewAction.ShouldBe(OctopusImportPreviewAction.Create), r => r.Choice.ShouldNotBeNull());
    }

    [Fact]
    public void Merge_NoClientPlan_IsTheServerPlan()
    {
        var server = Plan(Choice("Steps-2", OctopusImportPreviewAction.Skip, null));

        OctopusImportServerOwnedRows.MergeServerOwnedRows(null, server).ShouldBeSameAs(server);
    }

    private static OctopusImportPreviewPlanDto Plan(params OctopusImportResourceResultDto[] rows) => new() { Resources = rows.ToList() };

    private static OctopusImportResourceResultDto Row(string sourceId, string sourceType, OctopusImportPreviewAction action) => new() { SourceId = sourceId, SourceType = sourceType, PreviewAction = action };

    private static OctopusImportResourceResultDto Choice(string sourceId, OctopusImportPreviewAction action, string follows)
        => new() { SourceId = sourceId, SourceType = "DeploymentStep", PreviewAction = action, Choice = new OctopusImportChoiceDto { Reason = OctopusImportChoiceReason.AddedInOctopus, Selected = action == OctopusImportPreviewAction.Create, FollowsSourceId = follows } };
}
