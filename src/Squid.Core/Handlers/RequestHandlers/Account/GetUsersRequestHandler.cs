using Squid.Core.Services.Account;
using Squid.Message.Requests.Account;

namespace Squid.Core.Handlers.RequestHandlers.Account;

public class GetUsersRequestHandler : IRequestHandler<GetUsersRequest, GetUsersResponse>
{
    private readonly IAccountService _accountService;

    public GetUsersRequestHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public async Task<GetUsersResponse> Handle(IReceiveContext<GetUsersRequest> context, CancellationToken cancellationToken)
    {
        var users = await _accountService.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return new GetUsersResponse
        {
            Data = users
        };
    }
}
