using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.LifeCycle;

public class CreateLifeCycleCommandHandler : ICommandHandler<CreateLifeCycleCommand, CreateLifeCycleResponse>
{
    private readonly ILifeCycleService _lifeCycleService;

    public CreateLifeCycleCommandHandler(ILifeCycleService lifeCycleService)
    {
        _lifeCycleService = lifeCycleService;
    }

    public async Task<CreateLifeCycleResponse> Handle(IReceiveContext<CreateLifeCycleCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _lifeCycleService.CreateLifeCycleAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateLifeCycleResponse
        {
            Data = @event.Data
        };
    }
}