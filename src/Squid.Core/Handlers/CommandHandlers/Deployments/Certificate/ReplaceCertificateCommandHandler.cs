using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Commands.Deployments.Certificate;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Certificate;

public class ReplaceCertificateCommandHandler : ICommandHandler<ReplaceCertificateCommand, ReplaceCertificateResponse>
{
    private readonly ICertificateService _certificateService;

    public ReplaceCertificateCommandHandler(ICertificateService certificateService)
    {
        _certificateService = certificateService;
    }

    public async Task<ReplaceCertificateResponse> Handle(IReceiveContext<ReplaceCertificateCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _certificateService.ReplaceCertificateAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new ReplaceCertificateResponse
        {
            Data = new ReplaceCertificateResponseData
            {
                Certificate = @event.Data
            }
        };
    }
}
