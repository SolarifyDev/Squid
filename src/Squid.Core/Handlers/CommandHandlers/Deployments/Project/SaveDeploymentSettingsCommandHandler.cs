using Squid.Core.Services.Deployments.Project;
using Squid.Message.Commands.Deployments.Project;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Project;

public class SaveDeploymentSettingsCommandHandler(IProjectDeploymentSettingsService deploymentSettingsService)
    : ICommandHandler<SaveDeploymentSettingsCommand, SaveDeploymentSettingsResponse>
{
    public async Task<SaveDeploymentSettingsResponse> Handle(IReceiveContext<SaveDeploymentSettingsCommand> context, CancellationToken cancellationToken)
    {
        var saved = await deploymentSettingsService.SaveAsync(context.Message.ProjectId, context.Message.DeploymentSettings, cancellationToken).ConfigureAwait(false);

        return new SaveDeploymentSettingsResponse { Data = saved };
    }
}
