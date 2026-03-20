using Squid.Message.Models.Authorization;
using Squid.Message.Response;

namespace Squid.Message.Requests.Authorization;

public class GetPermissionsRequest : IRequest
{
}

public class GetPermissionsResponse : SquidResponse<List<PermissionDto>>
{
}
