using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Commands.Deployments.Certificate;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Certificate;

public class CreateCertificateCommandHandler : ICommandHandler<CreateCertificateCommand, CreateCertificateResponse>
{
    private readonly ICertificateService _certificateService;

    public CreateCertificateCommandHandler(ICertificateService certificateService)
    {
        _certificateService = certificateService;
    }

    public async Task<CreateCertificateResponse> Handle(IReceiveContext<CreateCertificateCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _certificateService.CreateCertificateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new CreateCertificateResponse
        {
            Data = new CreateCertificateResponseData
            {
                Certificate = @event.Data
            }
        };
    }
}
