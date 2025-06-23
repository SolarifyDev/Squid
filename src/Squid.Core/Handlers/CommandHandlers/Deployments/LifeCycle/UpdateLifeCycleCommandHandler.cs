using Squid.Message.Commands.Deployments.LifeCycle;
using Squid.Core.Services.Deployments.LifeCycle;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.LifeCycle;

public class UpdateLifeCycleCommandHandler : ICommandHandler<UpdateLifeCycleCommand, UpdateLifeCycleResponse>
{
    private readonly ILifeCycleService _lifeCycleService;

    public UpdateLifeCycleCommandHandler(ILifeCycleService lifeCycleService)
    {
        _lifeCycleService = lifeCycleService;
    }

    public async Task<UpdateLifeCycleResponse> Handle(IReceiveContext<UpdateLifeCycleCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _lifeCycleService.UpdateLifeCycleAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateLifeCycleResponse
        {
            Data = @event.Data
        };
    }
}