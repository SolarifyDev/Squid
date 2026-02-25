using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Channels;
using Squid.Core.Services.Deployments.Project;

namespace Squid.Core.Services.Deployments.LifeCycle;

public interface ILifecycleResolver : IScopedDependency
{
    Task<Lifecycle> ResolveLifecycleAsync(int projectId, int channelId, CancellationToken cancellationToken);
}

public class LifecycleResolver(
    IChannelDataProvider channelDataProvider,
    IProjectDataProvider projectDataProvider,
    ILifeCycleDataProvider lifeCycleDataProvider) : ILifecycleResolver
{
    public async Task<Lifecycle> ResolveLifecycleAsync(int projectId, int channelId, CancellationToken cancellationToken)
    {
        var channel = await channelDataProvider.GetChannelByIdAsync(channelId, cancellationToken).ConfigureAwait(false);

        var lifecycleId = channel?.LifecycleId;

        if (lifecycleId == null)
        {
            var project = await projectDataProvider.GetProjectByIdAsync(projectId, cancellationToken).ConfigureAwait(false);

            if (project == null)
                throw new InvalidOperationException($"Project {projectId} not found");

            lifecycleId = project.LifecycleId;
        }

        var lifecycle = await lifeCycleDataProvider.GetLifecycleByIdAsync(lifecycleId.Value, cancellationToken).ConfigureAwait(false);

        if (lifecycle == null)
            throw new InvalidOperationException($"Lifecycle {lifecycleId} not found");

        return lifecycle;
    }
}
