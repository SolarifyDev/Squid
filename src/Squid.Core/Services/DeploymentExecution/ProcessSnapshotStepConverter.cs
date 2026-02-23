using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;

namespace Squid.Core.Services.DeploymentExecution;

public static class ProcessSnapshotStepConverter
{
    public static List<DeploymentStepDto> Convert(DeploymentProcessSnapshotDto processSnapshot)
    {
        var steps = new List<DeploymentStepDto>();

        foreach (var stepSnap in processSnapshot.Data.StepSnapshots.OrderBy(p => p.StepOrder))
        {
            var step = new DeploymentStepDto
            {
                Id = stepSnap.Id,
                ProcessId = processSnapshot.Id,
                StepOrder = stepSnap.StepOrder,
                Name = stepSnap.Name,
                StepType = stepSnap.StepType,
                Condition = stepSnap.Condition,
                StartTrigger = stepSnap.StartTrigger ?? string.Empty,
                PackageRequirement = string.Empty,
                IsDisabled = stepSnap.IsDisabled,
                IsRequired = stepSnap.IsRequired,
                CreatedAt = stepSnap.CreatedAt,
                Properties = stepSnap.Properties.Select(
                    kvp => new DeploymentStepPropertyDto
                    {
                        StepId = stepSnap.Id,
                        PropertyName = kvp.Key,
                        PropertyValue = kvp.Value
                    }).ToList(),
                Actions = stepSnap.ActionSnapshots.Select(
                    action => new DeploymentActionDto
                    {
                        Id = action.Id,
                        StepId = stepSnap.Id,
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
                        Properties = action.Properties.Select(
                            kvp => new DeploymentActionPropertyDto
                            {
                                ActionId = action.Id,
                                PropertyName = kvp.Key,
                                PropertyValue = kvp.Value
                            }).ToList(),
                        Environments = action.Environments ?? new List<int>(),
                        ExcludedEnvironments = action.ExcludedEnvironments ?? new List<int>(),
                        Channels = action.Channels ?? new List<int>()
                    }).ToList()
            };

            steps.Add(step);
        }

        return steps;
    }
}
