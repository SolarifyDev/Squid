using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Squid.Message.Constants;

namespace Squid.Core.Services.Identity;

public interface ICurrentUser
{
    int? Id { get; }

    string Name { get; }

    /// <summary>
    /// True when this user represents a TRUSTED background-job / internal
    /// context (e.g. <see cref="InternalUser"/>) rather than an
    /// HTTP-request-derived identity. Authorization middleware uses this
    /// to bypass permission checks for internal callers WITHOUT relying
    /// on the ID-equals-8888 heuristic that the pre-D.6 code used (the
    /// heuristic failed open when <see cref="ApiUser"/> sat in a non-HTTP
    /// scope and silently returned 8888 too).
    /// </summary>
    bool IsInternal { get; }
}

public class ApiUser : ICurrentUser, IScopedDependency
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
            // P1-D.6 (Phase-7): pre-fix this returned CurrentUsers.InternalUser.Id
            // when HttpContext was null, which made the authorization middleware's
            // "skip checks for internal user (Id == 8888)" heuristic fail open
            // for any ApiUser stuck in a non-HTTP DI scope (background job
            // misconfig, test-mode leak). Now we return null instead — the
            // middleware's fail-closed null-Id guard will reject.
            if (_httpContextAccessor?.HttpContext == null) return null;

            var currentAuthScheme = _httpContextAccessor.HttpContext.User.Identity?.AuthenticationType;

            var idClaim = _httpContextAccessor.HttpContext.User.Claims
                .FirstOrDefault(x => x.Type == ClaimTypes.NameIdentifier && x.Subject?.AuthenticationType == currentAuthScheme)?.Value;

            return int.TryParse(idClaim, out var id) ? id : null;
        }
    }

    public string Name
    {
        get
        {
            if (_httpContextAccessor?.HttpContext == null) return string.Empty;

            var currentAuthScheme = _httpContextAccessor.HttpContext?.User.Identity?.AuthenticationType;

            return _httpContextAccessor?.HttpContext?.User.Claims
                .FirstOrDefault(x => x.Type == ClaimTypes.Name && x.Subject?.AuthenticationType == currentAuthScheme)?.Value ?? string.Empty;
        }
    }

    /// <summary>
    /// Always false for ApiUser — this implementation is HTTP-bound.
    /// A non-HTTP scope resolving ApiUser is a DI configuration error;
    /// the resulting null Id will be rejected by AuthorizationSpecification.
    /// </summary>
    public bool IsInternal => false;
}

public class InternalUser : ICurrentUser
{
    public int? Id => CurrentUsers.InternalUser.Id;

    public string Name => CurrentUsers.InternalUser.Name;

    /// <summary>True — InternalUser is the canonical trusted internal
    /// context (background jobs, system tasks). The new authorization
    /// middleware bypass keys off this property, NOT off Id-equals-8888,
    /// so a stray ApiUser with the same number can no longer impersonate
    /// internal context.</summary>
    public bool IsInternal => true;
}
