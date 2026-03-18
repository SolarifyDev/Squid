using Squid.Message.Response;

namespace Squid.Message.Requests.Account;

public class GetMyApiKeysRequest : IRequest
{
}

public class GetMyApiKeysResponse : SquidResponse<GetMyApiKeysResponseData>
{
}

public class GetMyApiKeysResponseData
{
    public List<ApiKeyDto> ApiKeys { get; set; } = new();
}

public class ApiKeyDto
{
    public int Id { get; set; }
    public string MaskedKey { get; set; }
    public string Description { get; set; }
    public DateTimeOffset CreatedDate { get; set; }
}
