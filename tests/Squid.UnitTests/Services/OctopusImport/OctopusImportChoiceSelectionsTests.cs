using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportChoiceSelectionsTests
{
    // Only a root row's own flag is a selection: a follower is decided with its root, and a flag on a row without a choice is never read.
    [Fact]
    public void From_ReadsOnlyRootChoiceRowsWithAFlag()
    {
        var plan = new OctopusImportPreviewPlanDto
        {
            Resources =
            [
                Row("Steps-1", new OctopusImportChoiceDto { Reason = OctopusImportChoiceReason.AddedInOctopus, Selected = true }),
                Row("Actions-1", new OctopusImportChoiceDto { Reason = OctopusImportChoiceReason.AddedInOctopus, Selected = true, FollowsSourceId = "Steps-1" }),
                Row("Steps-2", new OctopusImportChoiceDto { Reason = OctopusImportChoiceReason.DeletedInSquid, Selected = null }),
                Row("Steps-3", null)
            ]
        };

        var selections = OctopusImportChoiceSelections.From(plan);

        selections.SelectedOr("steps-1", false).ShouldBeTrue("source ids are matched ignoring case");
        selections.SelectedOr("Actions-1", false).ShouldBeFalse("a follower carries no selection of its own");
        selections.SelectedOr("Steps-2", true).ShouldBeTrue("a missing flag means the server default");
        selections.SelectedOr("Steps-3", false).ShouldBeFalse("a row without a choice carries no selection");
    }

    [Fact]
    public void From_NullPlanOrOldClientPlan_IsEmpty()
    {
        OctopusImportChoiceSelections.From(null).SelectedOr("Steps-1", true).ShouldBeTrue();
        OctopusImportChoiceSelections.From(new OctopusImportPreviewPlanDto { Resources = [Row("Steps-1", null)] }).SelectedOr("Steps-1", false).ShouldBeFalse();
        OctopusImportChoiceSelections.Empty.SelectedOr(null, true).ShouldBeTrue();
    }

    private static OctopusImportResourceResultDto Row(string sourceId, OctopusImportChoiceDto choice) => new() { SourceId = sourceId, SourceType = "DeploymentStep", Choice = choice };
}
