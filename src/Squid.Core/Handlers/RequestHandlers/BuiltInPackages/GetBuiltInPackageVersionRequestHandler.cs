using Squid.Core.Services.Packages.BuiltIn;
using Squid.Message.Requests.BuiltInPackages;

namespace Squid.Core.Handlers.RequestHandlers.BuiltInPackages;

public class GetBuiltInPackageVersionRequestHandler(IBuiltInPackageQueryService query) : IRequestHandler<GetBuiltInPackageVersionRequest, GetBuiltInPackageVersionResponse>
{
    public async Task<GetBuiltInPackageVersionResponse> Handle(IReceiveContext<GetBuiltInPackageVersionRequest> context, CancellationToken cancellationToken)
    {
        var data = await query.GetVersionAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new GetBuiltInPackageVersionResponse { Data = data };
    }
}
