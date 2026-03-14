using System.Collections.Generic;
using Squid.Core.Services.Deployments.Interruptions;
using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Interruption;

namespace Squid.UnitTests.Services.Deployments.Interruptions;

public class InterruptionFormBuilderTests
{
    [Fact]
    public void BuildGuidedFailureForm_ReturnsThreeElements()
    {
        var form = InterruptionFormBuilder.BuildGuidedFailureForm("Step 1", "Action 1", "machine-1", "connection refused");

        form.Elements.Count.ShouldBe(3);
        form.Elements[0].Name.ShouldBe("Guidance");
        form.Elements[0].Control.ShouldBeOfType<ParagraphControl>();
        form.Elements[1].Name.ShouldBe("Notes");
        form.Elements[1].Control.ShouldBeOfType<TextAreaControl>();
        form.Elements[2].Name.ShouldBe("Result");

        var buttons = form.Elements[2].Control.ShouldBeOfType<SubmitButtonGroupControl>();
        buttons.Buttons.ShouldBe(new List<string> { "Abort", "Retry", "Ignore" });
    }

    [Fact]
    public void BuildManualInterventionForm_ReturnsThreeElements()
    {
        var form = InterruptionFormBuilder.BuildManualInterventionForm("Please verify the deployment");

        form.Elements.Count.ShouldBe(3);
        form.Elements[0].Name.ShouldBe("Instructions");

        var paragraph = form.Elements[0].Control.ShouldBeOfType<ParagraphControl>();
        paragraph.Text.ShouldBe("Please verify the deployment");

        form.Elements[1].Name.ShouldBe("Notes");
        form.Elements[2].Name.ShouldBe("Result");

        var buttons = form.Elements[2].Control.ShouldBeOfType<SubmitButtonGroupControl>();
        buttons.Buttons.ShouldBe(new List<string> { "Proceed", "Abort" });
    }

    [Theory]
    [InlineData(InterruptionType.GuidedFailure, "Retry", InterruptionOutcome.Retry)]
    [InlineData(InterruptionType.GuidedFailure, "Ignore", InterruptionOutcome.Skip)]
    [InlineData(InterruptionType.GuidedFailure, "Abort", InterruptionOutcome.Abort)]
    [InlineData(InterruptionType.ManualIntervention, "Proceed", InterruptionOutcome.Proceed)]
    [InlineData(InterruptionType.ManualIntervention, "Abort", InterruptionOutcome.Abort)]
    public void ResolveOutcome_MapsButtonToExpectedOutcome(InterruptionType type, string button, InterruptionOutcome expected)
    {
        var values = new Dictionary<string, string> { ["Result"] = button };

        var outcome = InterruptionFormBuilder.ResolveOutcome(type, values);

        outcome.ShouldBe(expected);
    }

    [Theory]
    [InlineData(InterruptionType.GuidedFailure)]
    [InlineData(InterruptionType.ManualIntervention)]
    public void ResolveOutcome_UnknownValue_ReturnsAbort(InterruptionType type)
    {
        var values = new Dictionary<string, string> { ["Result"] = "UnknownButton" };

        var outcome = InterruptionFormBuilder.ResolveOutcome(type, values);

        outcome.ShouldBe(InterruptionOutcome.Abort);
    }

    [Theory]
    [InlineData(InterruptionType.GuidedFailure)]
    [InlineData(InterruptionType.ManualIntervention)]
    public void ResolveOutcome_NullValues_ReturnsAbort(InterruptionType type)
    {
        var outcome = InterruptionFormBuilder.ResolveOutcome(type, null);

        outcome.ShouldBe(InterruptionOutcome.Abort);
    }
}
