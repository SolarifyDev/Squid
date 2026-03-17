using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Requests.Authorization;

public class GetMyPermissionsRequest : IRequest
{
}

public class GetMyPermissionsResponse : SquidResponse<UserPermissionSetDto>
{
}
