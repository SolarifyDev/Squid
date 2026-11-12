using System.Linq;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.Deployments;
using Squid.Message.Enums.Events;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportHistoryChildMapperTests
{
    private static readonly DateTimeOffset Completed = new(2024, 7, 10, 9, 48, 11, TimeSpan.Zero);

    [Theory]
    [InlineData("DeploymentQueued", EventCategory.DeploymentQueued)]
    [InlineData("DeploymentStarted", EventCategory.DeploymentStarted)]
    [InlineData("DeploymentSucceeded", EventCategory.DeploymentSucceeded)]
    [InlineData("DeploymentFailed", EventCategory.DeploymentFailed)]
    [InlineData("DeploymentCanceled", EventCategory.DeploymentCanceled)]
    [InlineData("DeploymentTimedOut", EventCategory.DeploymentTimedOut)]
    [InlineData("ManualInterventionInterruptionRaised", EventCategory.ManualInterventionRaised)]
    [InlineData("ManualInterventionSubmitted", EventCategory.ManualInterventionSubmitted)]
    [InlineData("GuidedFailureInterruptionRaised", EventCategory.GuidedFailureRaised)]
    [InlineData("DeploymentResumed", EventCategory.DeploymentResumed)]
    [InlineData("deploymentsucceeded", EventCategory.DeploymentSucceeded)]
    [InlineData("MachineHealthy", EventCategory.ImportedOctopusEvent)]
    [InlineData(null, EventCategory.ImportedOctopusEvent)]
    public void MapEventCategory_Theory(string octopusCategory, EventCategory expected)
        => OctopusImportHistoryChildMapper.MapEventCategory(octopusCategory).ShouldBe(expected);

    [Theory]
    [InlineData("Success", EventCategory.DeploymentSucceeded)]
    [InlineData("Failed", EventCategory.DeploymentFailed)]
    [InlineData("Canceled", EventCategory.DeploymentCanceled)]
    [InlineData("TimedOut", EventCategory.DeploymentTimedOut)]
    public void NoOctopusEvents_SynthesizesOneTerminalEvent(string state, EventCategory expected)
    {
        var events = OctopusImportHistoryChildMapper.MapEvents(null, OctopusImportTaskStateNormalizer.Normalize(state), Completed, "levi.y");

        var single = events.ShouldHaveSingleItem();
        single.Category.ShouldBe(expected);
        single.Occurred.ShouldBe(Completed);
        single.Username.ShouldBe("levi.y");
        single.OctopusCategory.ShouldBeNull();
    }

    [Theory]
    [InlineData("Executing")]
    [InlineData("Queued")]
    public void NonTerminalTask_SynthesizesNoEvent(string state)
        => OctopusImportHistoryChildMapper.MapEvents([], OctopusImportTaskStateNormalizer.Normalize(state), Completed, "levi.y").ShouldBeEmpty();

    [Fact]
    public void OctopusEvents_ReplaceTheSynthesizedOne_KeepingUnknownCategories()
    {
        var events = new List<OctopusEventDto>
        {
            new() { Id = "Events-2", Category = "DeploymentSucceeded", Occurred = Completed, Username = "alice" },
            new() { Id = "Events-1", Category = "TenantVariablesModified", Message = "custom", Occurred = Completed.AddMinutes(-1) }
        };

        var mapped = OctopusImportHistoryChildMapper.MapEvents(events, OctopusImportTaskStateNormalizer.Normalize("Success"), Completed, "levi.y");

        mapped.Select(e => e.Category).ShouldBe([EventCategory.ImportedOctopusEvent, EventCategory.DeploymentSucceeded]);
        mapped[0].OctopusCategory.ShouldBe("TenantVariablesModified");
        mapped[0].Message.ShouldBe("custom");
        mapped[0].Username.ShouldBe("levi.y");
        mapped[1].Username.ShouldBe("alice");
    }

    // The category and message are serialized into the jsonb references, where even an escaped U+0000 is rejected.
    [Fact]
    public void OctopusEvents_WithNulInCategoryOrMessage_AreStripped()
    {
        var events = new List<OctopusEventDto> { new() { Id = "Events-1", Category = "Custom\0Category", Message = "tool\0output", Occurred = Completed } };

        var mapped = OctopusImportHistoryChildMapper.MapEvents(events, OctopusImportTaskStateNormalizer.Normalize("Success"), Completed, "levi.y").ShouldHaveSingleItem();

        mapped.OctopusCategory.ShouldBe("CustomCategory");
        mapped.Message.ShouldBe("tooloutput");
    }

    [Fact]
    public void ResolvedInterruptions_BecomeRows_PendingAndOtherTypesStayInJson()
    {
        var details = new OctopusTaskHistoryDetails
        {
            Interruptions =
            [
                new OctopusInterruptionDto { Id = "Interruptions-1", Title = "Approve", Type = "ManualIntervention", IsPending = false, Created = Completed.AddMinutes(-5), ResponsibleTeamIds = ["Teams-1", "Teams-404"], Form = new OctopusInterruptionFormDto { Values = new Dictionary<string, string> { ["Result"] = "Proceed", ["Notes"] = "ok" } } },
                new OctopusInterruptionDto { Id = "Interruptions-2", Title = "Retry?", Type = "GuidedFailure", IsPending = false, Created = Completed.AddMinutes(-4), Form = new OctopusInterruptionFormDto { Values = new Dictionary<string, string> { ["Guidance"] = "Retry" } } },
                new OctopusInterruptionDto { Id = "Interruptions-3", Title = "Still waiting", Type = "ManualIntervention", IsPending = true, Created = Completed.AddMinutes(-3) },
                new OctopusInterruptionDto { Id = "Interruptions-4", Title = "Other", Type = "SomethingElse", IsPending = false }
            ],
            Events = [new OctopusEventDto { Id = "Events-9", Category = "ManualInterventionSubmitted", Occurred = Completed.AddMinutes(-2), RelatedDocumentIds = ["Interruptions-1"] }]
        };

        var rows = OctopusImportHistoryChildMapper.MapResolvedInterruptions(details, teamId => teamId == "Teams-1" ? 41 : null);

        rows.Count.ShouldBe(2);
        rows[0].Type.ShouldBe(InterruptionType.ManualIntervention);
        rows[0].Resolution.ShouldBe("Proceed");
        rows[0].ResolvedAt.ShouldBe(Completed.AddMinutes(-2));
        rows[0].ResponsibleTeamIds.ShouldBe("41");
        rows[0].SubmittedValuesJson.ShouldContain("Proceed");
        rows[1].Type.ShouldBe(InterruptionType.GuidedFailure);
        rows[1].Resolution.ShouldBe("Retry");
        rows[1].ResolvedAt.ShouldBe(Completed.AddMinutes(-4));
        rows[1].ResponsibleTeamIds.ShouldBeNull();

        OctopusImportHistoryChildMapper.PendingInterruptions(details).Count.ShouldBe(2);
    }

    [Fact]
    public void ResolvedInterruption_WithoutFormValues_IsResolved()
    {
        var details = new OctopusTaskHistoryDetails { Interruptions = [new OctopusInterruptionDto { Id = "Interruptions-1", Type = "ManualIntervention", Created = Completed }] };

        OctopusImportHistoryChildMapper.MapResolvedInterruptions(details, _ => null).ShouldHaveSingleItem().Resolution.ShouldBe("Resolved");
    }
}
