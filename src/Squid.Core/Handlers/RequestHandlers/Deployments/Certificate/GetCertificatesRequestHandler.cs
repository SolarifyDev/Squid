using Squid.Core.Services.Deployments.Certificates;
using Squid.Message.Requests.Deployments.Certificate;

namespace Squid.Core.Handlers.RequestHandlers.Deployments.Certificate;

public class GetCertificatesRequestHandler : IRequestHandler<GetCertificatesRequest, GetCertificatesResponse>
{
    private readonly ICertificateService _certificateService;

    public GetCertificatesRequestHandler(ICertificateService certificateService)
    {
        _certificateService = certificateService;
    }

    public async Task<GetCertificatesResponse> Handle(IReceiveContext<GetCertificatesRequest> context, CancellationToken cancellationToken)
    {
        return await _certificateService.GetCertificatesAsync(context.Message, cancellationToken).ConfigureAwait(false);
    }
}
