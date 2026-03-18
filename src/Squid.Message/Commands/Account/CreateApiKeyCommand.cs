using Squid.Message.Response;

namespace Squid.Message.Commands.Account;

public class CreateApiKeyCommand : ICommand
{
    public string Description { get; set; }
}

public class CreateApiKeyResponse : SquidResponse<CreateApiKeyResponseData>
{
}

public class CreateApiKeyResponseData
{
    public int Id { get; set; }
    public string ApiKey { get; set; }
    public string Description { get; set; }
}
