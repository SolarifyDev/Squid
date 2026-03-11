using Squid.Message.Enums.Deployments;
using Squid.Message.Models.Deployments.Interruption;

namespace Squid.Core.Services.Deployments.Interruptions;

public static class InterruptionFormBuilder
{
    public static InterruptionForm BuildGuidedFailureForm(string stepName, string actionName, string machineName, string errorMessage)
    {
        return new InterruptionForm
        {
            Elements = new List<InterruptionFormElement>
            {
                new() { Name = "Guidance", Control = new ParagraphControl { Text = $"Action \"{actionName}\" failed on {machineName} during step \"{stepName}\": {errorMessage}" } },
                new() { Name = "Notes", Control = new TextAreaControl { Label = "Notes" } },
                new() { Name = "Result", Control = new SubmitButtonGroupControl { Buttons = new List<string> { "Abort", "Retry", "Ignore" } } }
            }
        };
    }

    public static InterruptionForm BuildManualInterventionForm(string instructions)
    {
        return new InterruptionForm
        {
            Elements = new List<InterruptionFormElement>
            {
                new() { Name = "Instructions", Control = new ParagraphControl { Text = instructions } },
                new() { Name = "Notes", Control = new TextAreaControl { Label = "Notes" } },
                new() { Name = "Result", Control = new SubmitButtonGroupControl { Buttons = new List<string> { "Proceed", "Abort" } } }
            }
        };
    }

    public static InterruptionOutcome ResolveOutcome(InterruptionType type, Dictionary<string, string> submittedValues)
    {
        var result = submittedValues?.GetValueOrDefault("Result");

        return type switch
        {
            InterruptionType.GuidedFailure => result switch
            {
                "Retry" => InterruptionOutcome.Retry,
                "Ignore" => InterruptionOutcome.Skip,
                "Abort" => InterruptionOutcome.Abort,
                _ => InterruptionOutcome.Abort
            },
            InterruptionType.ManualIntervention => result switch
            {
                "Proceed" => InterruptionOutcome.Proceed,
                "Abort" => InterruptionOutcome.Abort,
                _ => InterruptionOutcome.Abort
            },
            _ => InterruptionOutcome.Abort
        };
    }
}
