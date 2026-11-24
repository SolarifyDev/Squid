using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Actions;

public sealed partial class TriggerActionDataProvider
{
    public IAsyncEnumerable<SuccessfulTriggerDeployment> ReadSuccessfulDeploymentsAsync(
        int spaceId, int projectId, int environmentId, int? channelId, int? deploymentId)
    {
        var completions = _repository.QueryNoTracking<DeploymentCompletion>(
            c => c.State == TaskState.Success && c.SpaceId == spaceId);
        var deployments = _repository.QueryNoTracking<Deployment>(d => d.ProjectId == projectId
            && d.EnvironmentId == environmentId && d.SpaceId == spaceId && d.MachineId == 0);
        var tasks = _repository.QueryNoTracking<ServerTask>(t => t.State == TaskState.Success
            && t.SpaceId == spaceId && t.ProjectId == projectId && t.EnvironmentId == environmentId);
        var releases = _repository.QueryNoTracking<Release>(r => r.ProjectId == projectId && r.SpaceId == spaceId);
        var channels = _repository.QueryNoTracking<Channel>(c => c.ProjectId == projectId && c.SpaceId == spaceId);
        var projects = _repository.QueryNoTracking<Project>(p => p.SpaceId == spaceId);
        var environments = _repository.QueryNoTracking<Persistence.Entities.Deployments.Environment>(
            e => e.SpaceId == spaceId);
        var catchups = _repository.QueryNoTracking<ProjectTriggerRun>(
            run => run.ActionType == ProjectTriggerActions.AutoDeploy);
        var query = from completion in completions
                    join deployment in deployments on completion.DeploymentId equals deployment.Id
                    join task in tasks on deployment.TaskId equals (int?)task.Id
                    join project in projects on deployment.ProjectId equals project.Id
                    join environment in environments on deployment.EnvironmentId equals environment.Id
                    join release in releases on deployment.ReleaseId equals release.Id
                    join channel in channels on release.ChannelId equals channel.Id
                    where deployment.ChannelId == channel.Id && (channelId == null || channel.Id == channelId)
                          && (deploymentId == null || deployment.Id == deploymentId)
                          && !catchups.Any(run => run.DeploymentId == deployment.Id)
                    orderby completion.CompletedTime descending, deployment.Id descending, completion.Id descending
                    select new SuccessfulTriggerDeployment(
                        deployment.Id, release.Id, release.Version, channel.Id, completion.CompletedTime,
                        deployment.ProcessSnapshotId ?? release.ProjectDeploymentProcessSnapshotId,
                        deployment.VariableSetSnapshotId ?? release.ProjectVariableSetSnapshotId, deployment.Json);

        return query.AsAsyncEnumerable();
    }
}
