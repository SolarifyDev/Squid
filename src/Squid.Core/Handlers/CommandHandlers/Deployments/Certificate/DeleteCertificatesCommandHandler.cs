using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Commands.Deployments.Certificate;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.Certificate;

public class DeleteCertificatesCommandHandler : ICommandHandler<DeleteCertificatesCommand, DeleteCertificatesResponse>
{
    private readonly ICertificateService _certificateService;

    public DeleteCertificatesCommandHandler(ICertificateService certificateService)
    {
        _certificateService = certificateService;
    }

    public async Task<DeleteCertificatesResponse> Handle(IReceiveContext<DeleteCertificatesCommand> context, CancellationToken cancellationToken)
    {
        var @event = await _certificateService.DeleteCertificatesAsync(context.Message, cancellationToken).ConfigureAwait(false);

        await context.PublishAsync(@event, cancellationToken).ConfigureAwait(false);

        return new DeleteCertificatesResponse
        {
            Data = @event.Data
        };
    }
}
