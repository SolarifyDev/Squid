using Microsoft.AspNetCore.Http;
using Shouldly;
using Squid.Core.Services.Identity;
using Squid.Message.Constants;
using Xunit;

namespace Squid.UnitTests.Services.Identity;

/// <summary>
/// P1-D.6 (Phase-7): pins the contract of <see cref="ICurrentUser"/>'s two
/// concrete implementations after the audit-driven refactor.
///
/// <para><b>The bug it closes</b>: pre-fix
/// <see cref="CurrentUsers.InternalUser.Id"/> was a mutable
/// <c>static int</c> (not <c>const</c>); <see cref="ApiUser.Id"/> fell back
/// to that value when <c>HttpContext == null</c>; and the authorization
/// middleware's bypass keyed off <c>Id == 8888</c>. Combined, those three
/// gave a "DI mishap → ApiUser sees null HttpContext → returns 8888 →
/// middleware silently bypasses every permission check" path.</para>
///
/// <para><b>Post-fix invariants</b>:</para>
/// <list type="bullet">
///   <item><see cref="CurrentUsers.InternalUser.Id"/> is <c>const</c> (no
///         mutable state, no race, no inheritance to override).</item>
///   <item><see cref="ApiUser.Id"/> returns <c>null</c> when HttpContext is
///         null — the middleware's null-Id guard then rejects.</item>
///   <item><see cref="ApiUser.IsInternal"/> is always <c>false</c>.</item>
///   <item><see cref="InternalUser.IsInternal"/> is always <c>true</c>.</item>
/// </list>
/// </summary>
public sealed class ICurrentUserTests
{
    [Fact]
    public void InternalUser_IdIsConst_PinnedAt8888()
    {
        // const value pin (Rule 8 + audit constant).
        CurrentUsers.InternalUser.Id.ShouldBe(8888);
    }

    [Fact]
    public void InternalUser_IsInternal_True()
    {
        var user = new InternalUser();
        user.IsInternal.ShouldBeTrue();
    }

    [Fact]
    public void InternalUser_Id_MatchesConstant()
    {
        var user = new InternalUser();
        user.Id.ShouldBe(CurrentUsers.InternalUser.Id);
    }

    [Fact]
    public void ApiUser_NullHttpContext_IdReturnsNull_NotInternalUserId()
    {
        // The actual D.6 regression: pre-fix this returned
        // CurrentUsers.InternalUser.Id (8888) — making it indistinguishable
        // from a real internal user to the (then-Id-keyed) middleware.
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns((HttpContext)null);

        var user = new ApiUser(accessor.Object);

        user.Id.ShouldBeNull(
            customMessage:
                "ApiUser stuck in a non-HTTP scope must surface NULL, not the InternalUser sentinel. " +
                "The middleware's null-Id fail-closed branch then rejects rather than bypasses.");
    }

    [Fact]
    public void ApiUser_NullHttpContext_NameReturnsEmpty_NotInternalUserName()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns((HttpContext)null);

        var user = new ApiUser(accessor.Object);

        user.Name.ShouldBe(string.Empty,
            customMessage: "ApiUser without HTTP context must NOT impersonate InternalUser's display name.");
    }

    [Fact]
    public void ApiUser_AlwaysReportsIsInternalFalse()
    {
        // Even if some code path managed to spoof an Id == 8888, the
        // IsInternal=false signal blocks the middleware bypass. This is the
        // structural protection — the type itself declares it cannot be
        // trusted as internal.
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns((HttpContext)null);

        var user = new ApiUser(accessor.Object);

        user.IsInternal.ShouldBeFalse();
    }
}
