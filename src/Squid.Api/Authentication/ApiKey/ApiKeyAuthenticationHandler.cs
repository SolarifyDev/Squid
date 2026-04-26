using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Squid.Core.Services.Account;
using Squid.Core.Services.Caching;
using Squid.Message.Constants;

namespace Squid.Api.Authentication.ApiKey;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly IAccountService _accountService;
    private readonly ICacheManager _cacheManager;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAccountService accountService,
        ICacheManager cacheManager)
        : base(options, logger, encoder)
    {
        _accountService = accountService;
        _cacheManager = cacheManager;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-API-KEY", out var values))
            return AuthenticateResult.NoResult();

        var apiKey = values.ToString();

        if (string.IsNullOrWhiteSpace(apiKey))
            return AuthenticateResult.NoResult();

        var cacheKey = $"auth:apikey:user:{apiKey}";
        var cacheSetting = new RedisCachingSetting(expiry: TimeSpan.FromHours(1));

        var matchedUser = await _cacheManager.GetOrAddAsync(cacheKey,
            async _ => await _accountService.GetByApiKeyAsync(apiKey).ConfigureAwait(false),
            cacheSetting, Context.RequestAborted).ConfigureAwait(false);

        if (matchedUser == null)
            return AuthenticateResult.NoResult();

        // P1-D.5 (Phase-7): re-check IsDisabled at cache HIT time. Pre-fix
        // the 1-hour TTL kept disabled users authenticated for up to an hour
        // after out-of-band disable (direct SQL UPDATE, mutation paths that
        // don't call the cache-invalidating UpdateUserStatusAsync, etc.).
        // If the cached entry says disabled, return NoResult AND invalidate
        // the cache so the next request re-fetches fresh state.
        if (matchedUser.IsDisabled)
        {
            await _cacheManager.RemoveAsync(cacheKey, cacheSetting, Context.RequestAborted).ConfigureAwait(false);
            return AuthenticateResult.NoResult();
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, matchedUser.Id.ToString()),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(matchedUser.UserName) ? $"apikey_user_{matchedUser.Id}" : matchedUser.UserName)
        }, AuthenticationSchemeConstants.ApiKeyAuthenticationScheme);

        var claimsPrincipal = new ClaimsPrincipal(identity);
        var authenticationTicket = new AuthenticationTicket(claimsPrincipal, new AuthenticationProperties { IsPersistent = false }, Scheme.Name);

        Request.HttpContext.User = claimsPrincipal;

        return AuthenticateResult.Success(authenticationTicket);
    }
}
