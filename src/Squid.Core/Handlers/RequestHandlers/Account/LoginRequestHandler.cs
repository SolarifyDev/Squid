using Squid.Core.Services.Account;
using Squid.Message.Requests.Account;

namespace Squid.Core.Handlers.RequestHandlers.Account;

public class LoginRequestHandler : IRequestHandler<LoginRequest, LoginResponse>
{
    private readonly IAccountService _accountService;

    public LoginRequestHandler(IAccountService accountService)
    {
        _accountService = accountService;
    }

    public async Task<LoginResponse> Handle(IReceiveContext<LoginRequest> context, CancellationToken cancellationToken)
    {
        var result = await _accountService.LoginAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new LoginResponse
        {
            Data = result
        };
    }
}
