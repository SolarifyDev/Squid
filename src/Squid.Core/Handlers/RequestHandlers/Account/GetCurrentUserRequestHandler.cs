using Squid.Core.Services.Account;
using Squid.Message.Requests.Account;

namespace Squid.Core.Handlers.RequestHandlers.Account;

public class GetCurrentUserRequestHandler : IRequestHandler<GetCurrentUserRequest, GetCurrentUserResponse>
{
    private readonly IAccountService _accountService;

    public GetCurrentUserRequestHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public async Task<GetCurrentUserResponse> Handle(IReceiveContext<GetCurrentUserRequest> context, CancellationToken cancellationToken)
    {
        var user = await _accountService.GetByIdAsync(context.Message.UserId, cancellationToken).ConfigureAwait(false);

        return new GetCurrentUserResponse
        {
            Data = user
        };
    }
}
