using Squid.Core.Services.Deployments.Environments;
using Squid.Message.Commands.Deployments.Environment;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Environment;

public class DeleteEnvironmentsCommandHandler : ICommandHandler<DeleteEnvironmentsCommand, DeleteEnvironmentsResponse>
{
    private readonly IEnvironmentService _environmentService;

    public DeleteEnvironmentsCommandHandler(IEnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    public async Task<DeleteEnvironmentsResponse> Handle(IReceiveContext<DeleteEnvironmentsCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _environmentService.DeleteEnvironmentsAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteEnvironmentsResponse
        {
            Data = @event.Data
        };
    }
}
