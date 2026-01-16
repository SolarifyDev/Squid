using Squid.Core.Services.Deployments.Environments;
using Squid.Message.Commands.Deployments.Environment;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Environment;

public class CreateEnvironmentCommandHandler : ICommandHandler<CreateEnvironmentCommand, CreateEnvironmentResponse>
{
    private readonly IEnvironmentService _environmentService;

    public CreateEnvironmentCommandHandler(IEnvironmentService environmentService)
    {
        _environmentService = environmentService;
    }

    public async Task<CreateEnvironmentResponse> Handle(IReceiveContext<CreateEnvironmentCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _environmentService.CreateEnvironmentAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateEnvironmentResponse
        {
            Data = new CreateEnvironmentResponseData
            {
                Environment = @event.Data
            }
        };
    }
}
