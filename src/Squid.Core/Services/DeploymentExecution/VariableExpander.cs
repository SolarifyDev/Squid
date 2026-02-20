using Squid.Core.VariableSubstitution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution;

public static class VariableExpander
{
    public static DeploymentActionDto ExpandActionProperties(DeploymentActionDto action, VariableDictionary variableDictionary)
    {
        if (action == null)
            return null;

        var clone = new DeploymentActionDto
        {
            Id = action.Id,
            StepId = action.StepId,
            ActionOrder = action.ActionOrder,
            Name = action.Name,
            ActionType = action.ActionType,
            WorkerPoolId = action.WorkerPoolId,
            FeedId = action.FeedId,
            PackageId = action.PackageId,
            IsDisabled = action.IsDisabled,
            IsRequired = action.IsRequired,
            CanBeUsedForProjectVersioning = action.CanBeUsedForProjectVersioning,
            CreatedAt = action.CreatedAt,
            Environments = action.Environments,
            ExcludedEnvironments = action.ExcludedEnvironments,
            Channels = action.Channels,
            Properties = action.Properties.Select(p => new DeploymentActionPropertyDto
            {
                Id = p.Id,
                ActionId = p.ActionId,
                PropertyName = p.PropertyName,
                PropertyValue = ExpandString(p.PropertyValue, variableDictionary) ?? p.PropertyValue
            }).ToList()
        };

        return clone;
    }

    public static string ExpandString(string input, VariableDictionary variableDictionary)
    {
        if (input == null)
            return null;

        return variableDictionary.Evaluate(input);
    }
}
