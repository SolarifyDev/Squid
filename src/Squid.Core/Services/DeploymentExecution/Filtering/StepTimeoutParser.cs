using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Filtering;

public static class StepTimeoutParser
{
    public static TimeSpan? ParseTimeout(DeploymentStepDto step)
    {
        var property = step.Properties?.FirstOrDefault(p => p.PropertyName == SpecialVariables.Step.Timeout);

        if (property == null || string.IsNullOrEmpty(property.PropertyValue))
            return null;

        if (TimeSpan.TryParse(property.PropertyValue, out var value) && value > TimeSpan.Zero)
            return value;

        return null;
    }
}
