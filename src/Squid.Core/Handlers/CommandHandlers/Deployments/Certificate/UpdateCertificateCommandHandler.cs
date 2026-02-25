using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Commands.Deployments.Certificate;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Certificate;

public class UpdateCertificateCommandHandler : ICommandHandler<UpdateCertificateCommand, UpdateCertificateResponse>
{
    private readonly ICertificateService _certificateService;

    public UpdateCertificateCommandHandler(ICertificateService certificateService)
    {
        _certificateService = certificateService;
    }

    public async Task<UpdateCertificateResponse> Handle(IReceiveContext<UpdateCertificateCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _certificateService.UpdateCertificateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new UpdateCertificateResponse
        {
            Data = new UpdateCertificateResponseData
            {
                Certificate = @event.Data
            }
        };
    }
}
