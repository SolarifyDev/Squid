using System.Text.Encodings.Web;
using System.Threading;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Squid.Api.Authentication.ApiKey;
using Squid.Core.Services.Account;
using Squid.Core.Services.Caching;
using Squid.Message.Models.Account;
using Xunit;

namespace Squid.UnitTests.Authentication.ApiKey;

/// <summary>
/// P1-D.5 (Phase-7): pins the contract that an API-key cache hit MUST
/// re-check <see cref="UserAccountDto.IsDisabled"/> before authenticating.
///
/// <para><b>The bug</b>: pre-fix the handler trusted any cache hit
/// (1-hour TTL). If a user got disabled out-of-band — direct SQL UPDATE,
/// non-`UpdateUserStatusAsync` mutation path, password reset that didn't
/// invalidate, etc. — their stale cached entry kept authenticating new
/// requests for up to an hour. Fail-open security staleness window.</para>
///
/// <para><b>Fix</b>: add an <c>IsDisabled</c> guard at cache-hit time. If
/// the cached UserAccountDto says <c>IsDisabled = true</c>, the handler
/// returns <c>NoResult</c> AND invalidates the cache entry so the next
/// request re-fetches fresh.</para>
/// </summary>
public sealed class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task Authenticate_CacheHit_ActiveUser_Succeeds()
    {
        // Sanity: existing happy path still works.
        var (handler, accountService, cacheManager) = MakeHandler(
            cachedUser: new UserAccountDto { Id = 7, UserName = "alice", IsDisabled = false });

        await handler.InitializeAsync(NewScheme(), NewHttpContextWithApiKey("real-key"));
        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeTrue();
        result.Principal.Identity.IsAuthenticated.ShouldBeTrue();
    }

    [Fact]
    public async Task Authenticate_CacheHit_DisabledUser_ReturnsNoResult()
    {
        // The actual D.5 regression: cached `IsDisabled = true` must NOT
        // authenticate, regardless of TTL.
        var (handler, _, _) = MakeHandler(
            cachedUser: new UserAccountDto { Id = 7, UserName = "alice-disabled", IsDisabled = true });

        await handler.InitializeAsync(NewScheme(), NewHttpContextWithApiKey("real-key"));
        var result = await handler.AuthenticateAsync();

        result.Succeeded.ShouldBeFalse(
            customMessage:
                "P1-D.5 — disabled user must not authenticate even on cache hit. " +
                "Pre-fix the 1-hour TTL kept disabled accounts authenticated until cache expiry.");
        result.None.ShouldBeTrue();
    }

    [Fact]
    public async Task Authenticate_CacheHit_DisabledUser_InvalidatesCacheEntry()
    {
        // Stronger contract: detecting a disabled user on cache-hit must
        // also INVALIDATE the cache so subsequent requests re-fetch
        // (and pick up any further changes — e.g. user re-enabled,
        // user actually deleted) instead of repeatedly hitting the same
        // stale entry until TTL.
        var (handler, _, cacheManager) = MakeHandler(
            cachedUser: new UserAccountDto { Id = 7, UserName = "alice-disabled", IsDisabled = true });

        await handler.InitializeAsync(NewScheme(), NewHttpContextWithApiKey("real-key"));
        await handler.AuthenticateAsync();

        cacheManager.Verify(
            c => c.RemoveAsync(
                It.Is<string>(k => k.Contains("apikey") && k.Contains("real-key")),
                It.IsAny<ICachingSetting>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            failMessage: "stale-disabled cache entry must be invalidated so the next request re-fetches.");
    }

    [Fact]
    public async Task Authenticate_NoApiKey_NoResult()
    {
        var (handler, _, _) = MakeHandler(cachedUser: null);

        await handler.InitializeAsync(NewScheme(), NewHttpContextWithoutApiKey());
        var result = await handler.AuthenticateAsync();

        result.None.ShouldBeTrue();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (ApiKeyAuthenticationHandler Handler, Mock<IAccountService> AccountService, Mock<ICacheManager> CacheManager) MakeHandler(UserAccountDto cachedUser)
    {
        var optionsMonitor = new Mock<IOptionsMonitor<ApiKeyAuthenticationOptions>>();
        optionsMonitor.Setup(o => o.Get(It.IsAny<string>())).Returns(new ApiKeyAuthenticationOptions());

        var accountService = new Mock<IAccountService>();
        accountService.Setup(s => s.GetByApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedUser);

        var cacheManager = new Mock<ICacheManager>();
        // The handler calls `c.GetOrAddAsync(key, async _ => ..., setting, ct)`
        // — that's the Func<string, Task<T>> overload in ICacheManager.
        cacheManager.Setup(c => c.GetOrAddAsync(
                It.IsAny<string>(),
                It.IsAny<Func<string, Task<UserAccountDto>>>(),
                It.IsAny<ICachingSetting>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedUser);

        var handler = new ApiKeyAuthenticationHandler(
            optionsMonitor.Object,
            new NullLoggerFactory(),
            UrlEncoder.Default,
            accountService.Object,
            cacheManager.Object);

        return (handler, accountService, cacheManager);
    }

    private static AuthenticationScheme NewScheme()
        => new("ApiKey", null, typeof(ApiKeyAuthenticationHandler));

    private static HttpContext NewHttpContextWithApiKey(string apiKey)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers["X-API-KEY"] = apiKey;
        return ctx;
    }

    private static HttpContext NewHttpContextWithoutApiKey()
    {
        return new DefaultHttpContext();
    }
}
