using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Squid.Core.Constants;

namespace Squid.Infrastructure.Identity;

public interface ICurrentUser
{
    int? Id { get; }
    
    string Name { get; }
}

public class ApiUser : ICurrentUser
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ApiUser(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public int? Id
    {
        get
        {
            if (_httpContextAccessor?.HttpContext == null) return null;
            
            var currentAuthScheme = _httpContextAccessor.HttpContext.User.Identity?.AuthenticationType;
            
            var idClaim = _httpContextAccessor.HttpContext.User.Claims
                .SingleOrDefault(x => x.Type == ClaimTypes.NameIdentifier && x.Subject?.AuthenticationType == currentAuthScheme)?.Value;
            
            return int.TryParse(idClaim, out var id) ? id : null;
        }
    }

    public string Name
    {
        get
        {
            var currentAuthScheme = _httpContextAccessor.HttpContext.User.Identity?.AuthenticationType;
            
            return _httpContextAccessor?.HttpContext?.User.Claims.SingleOrDefault(x => x.Type == ClaimTypes.Name && x.Subject?.AuthenticationType == currentAuthScheme)?.Value;
        }
    }
}

public class InternalUser : ICurrentUser
{
    public int? Id => CurrentUsers.InternalUser.Id;

    public string Name => "internal_user";
}