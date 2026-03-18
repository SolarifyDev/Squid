using Squid.Message.Response;

namespace Squid.Message.Commands.Account;

public class DeleteApiKeyCommand : ICommand
{
    public int ApiKeyId { get; set; }
}

public class DeleteApiKeyResponse : SquidResponse
{
}
