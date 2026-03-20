using Squid.Core.Services.Account;
using Squid.Core.Services.Identity;
using Squid.Message.Requests.Account;

namespace Squid.Core.Handlers.RequestHandlers.Account;

public class GetMyApiKeysRequestHandler(IAccountService accountService, ICurrentUser currentUser) : IRequestHandler<GetMyApiKeysRequest, GetMyApiKeysResponse>
{
    public async Task<GetMyApiKeysResponse> Handle(IReceiveContext<GetMyApiKeysRequest> context, CancellationToken cancellationToken)
    {
        var keys = await accountService.GetApiKeysAsync(currentUser.Id!.Value, cancellationToken).ConfigureAwait(false);

        return new GetMyApiKeysResponse { Data = new GetMyApiKeysResponseData { ApiKeys = keys } };
    }
}
