using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.LifeCycle;

public class DeleteLifeCyclesCommandHandler : ICommandHandler<DeleteLifeCyclesCommand, DeleteLifeCyclesResponse>
{
    private readonly ILifeCycleService _lifeCycleService;

    public DeleteLifeCyclesCommandHandler(ILifeCycleService lifeCycleService)
    {
        _lifeCycleService = lifeCycleService;
    }

    public async Task<DeleteLifeCyclesResponse> Handle(IReceiveContext<DeleteLifeCyclesCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _lifeCycleService.DeleteLifeCyclesAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteLifeCyclesResponse
        {
            Data = @event.Data
        };
    }
}