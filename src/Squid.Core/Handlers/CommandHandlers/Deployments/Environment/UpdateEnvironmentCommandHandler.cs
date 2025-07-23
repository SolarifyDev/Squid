using Squid.Core.Services.Deployments.Environment;
using Squid.Message.Commands.Deployments.Environment;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Environment;

public class UpdateEnvironmentCommandHandler : ICommandHandler<UpdateEnvironmentCommand, UpdateEnvironmentResponse>
{
    private readonly IEnvironmentService _environmentService;

    public UpdateEnvironmentCommandHandler(IEnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    public async Task<UpdateEnvironmentResponse> Handle(IReceiveContext<UpdateEnvironmentCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _environmentService.UpdateEnvironmentAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateEnvironmentResponse
        {
            Data = new UpdateEnvironmentResponseData
            {
                Environment = @event.Data
            }
        };
    }
}
