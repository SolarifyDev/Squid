using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class StepTimeoutParser
{
    public const string TimeoutInMinutesLegacy = "Squid.Step.TimeoutInMinutes";

    public static TimeSpan? ParseTimeout(DeploymentStepDto step)
    {
        var timeoutProp = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.Timeout);
        if (timeoutProp != null && !string.IsNullOrWhiteSpace(timeoutProp.PropertyValue))
            return ParseTimeoutValue(timeoutProp.PropertyValue);

        var legacy = step.Properties?.FirstOrDefault(p => p.PropertyName == TimeoutInMinutesLegacy);
        if (legacy != null && int.TryParse(legacy.PropertyValue, out var minutes) && minutes > 0)
            return TimeSpan.FromMinutes(minutes);

        return null;
    }

    private static TimeSpan? ParseTimeoutValue(string raw)
    {
        if (int.TryParse(raw, out var seconds))
            return seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

        if (TimeSpan.TryParse(raw, out var value) && value > TimeSpan.Zero)
            return value;

        return null;
    }
}
