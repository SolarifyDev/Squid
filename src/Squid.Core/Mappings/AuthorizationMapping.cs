using Squid.Core.Persistence.Entities.Account;
using Squid.Message.Models.Authorization;

namespace Squid.Core.Mappings;

public class AuthorizationMapping : Profile
{
    public AuthorizationMapping()
    {
        CreateMap<UserRole, UserRoleDto>();
    }
}
