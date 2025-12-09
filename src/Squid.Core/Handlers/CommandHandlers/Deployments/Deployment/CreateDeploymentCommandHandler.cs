using Squid.Core.Services.Deployments;
using Squid.Message.Commands.Deployments.Deployment;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Deployment;

public class CreateDeploymentCommandHandler : ICommandHandler<CreateDeploymentCommand, CreateDeploymentResponse>
{
    private readonly IDeploymentService _deploymentService;

    public CreateDeploymentCommandHandler(IDeploymentService deploymentService)
    {
        _deploymentService = deploymentService;
    }

    public async Task<CreateDeploymentResponse> Handle(IReceiveContext<CreateDeploymentCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _deploymentService.CreateDeploymentAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateDeploymentResponse
        {
            Data = new CreateDeploymentResponseData
            {
                Deployment = @event.Deployment,
                TaskId = @event.TaskId
            }
        };
    }
}
