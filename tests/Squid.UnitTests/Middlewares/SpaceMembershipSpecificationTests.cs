using Mediator.Net.Context;
using Mediator.Net.Contracts;
using Microsoft.AspNetCore.Http;
using Squid.Core.Middlewares.SpaceScope;
using Squid.Core.Services.Identity;
using Squid.Message.Contracts;

namespace Squid.UnitTests.Middlewares;

/// <summary>
/// P0-Phase10.3 (audit D.3 / H-19) — pin the cross-space privesc gate.
///
/// <para><b>The privesc vector pre-Phase-10.3</b>: a user with team
/// membership in Space-1 sends a command with HTTP header
/// <c>X-Space-Id: 2</c>. SpaceIdInjectionSpecification trusted the header,
/// injected SpaceId=2 into the command body, and downstream authorization
/// checked permissions against THAT spoofed SpaceId. The user could
/// read/mutate Space-2 resources despite having no membership there —
/// the only TRUE cross-space privilege-escalation surface in the audit.</para>
///
/// <para>This middleware runs BEFORE the injection spec and rejects with
/// <see cref="CrossSpaceAccessDeniedException"/> when the header references
/// a Space the user isn't a member of. Internal users (Hangfire, system
/// tasks) bypass via <c>IsInternal=true</c> — same pattern as Phase-7 D.6.</para>
/// </summary>
// Tests in this class mutate SQUID_SPACE_MEMBERSHIP_ENFORCEMENT process-wide
// for mode-specific assertions. xUnit serializes methods within a class but
// runs classes in parallel — collection attribute ensures any other test class
// that ALSO mutates this env var queues behind us. None currently exists; the
// attribute is defensive future-proofing against the same issue Phase-9a
// uncovered with Serilog Log.Logger pollution.
[Collection("SpaceMembershipEnforcementEnvVarMutators")]
public sealed class SpaceMembershipSpecificationTests
{
    [Fact]
    public async Task PrivescAttempt_NonMember_HeaderSpaceId_Throws()
    {
        // The exact attack: user 1 (member of space 5) sends X-Space-Id: 99.
        // Resolver says NO → middleware throws CrossSpaceAccessDeniedException
        // BEFORE the injection spec ever sees the header.
        var spec = CreateSpec(headerValue: "99", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
        var message = new TestSpaceScopedRequest();
        var context = CreateContext(message);

        var ex = await Should.ThrowAsync<CrossSpaceAccessDeniedException>(
            () => spec.BeforeExecute(context.Object, CancellationToken.None));

        ex.UserId.ShouldBe(1);
        ex.RequestedSpaceId.ShouldBe(99);
    }

    [Fact]
    public async Task LegitAccess_Member_HeaderSpaceId_PassesThrough()
    {
        // User 1 IS a member of space 5; header X-Space-Id: 5 is legit.
        var spec = CreateSpec(headerValue: "5", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: true);
        var message = new TestSpaceScopedRequest();
        var context = CreateContext(message);

        await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
    }

    [Fact]
    public async Task InternalUser_Bypasses_RegardlessOfMembership()
    {
        // Hangfire / system tasks pass IsInternal=true — they don't have
        // team memberships, must bypass entirely (Phase-7 D.6 pattern).
        var spec = CreateSpec(headerValue: "5", currentUserId: 8888, isInternal: true, isMemberOfRequestedSpace: false);
        var message = new TestSpaceScopedRequest();
        var context = CreateContext(message);

        // Even though the resolver mock says "no membership", IsInternal short-circuits.
        await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
    }

    [Fact]
    public async Task NoHeader_FallsThrough_NoMembershipCheck()
    {
        // Header absent → middleware lets the message through. The injection
        // spec (running after this) will leave SpaceId at body-supplied value
        // or null. Membership check is irrelevant when no header is claimed.
        var spec = CreateSpec(headerValue: null, currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
        var message = new TestSpaceScopedRequest();
        var context = CreateContext(message);

        await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
    }

    [Fact]
    public async Task MalformedHeader_FallsThrough_NoMembershipCheck()
    {
        // Header value isn't an int. Treat as "no claim" and pass through —
        // the injection spec will reject the malformed header on its own.
        var spec = CreateSpec(headerValue: "not-an-int", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
        var message = new TestSpaceScopedRequest();
        var context = CreateContext(message);

        await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
    }

    [Fact]
    public async Task NullUserId_RejectsBeforeMembershipLookup()
    {
        // Phase-7 D.6 fail-closed posture: an ApiUser stuck in a non-HTTP
        // scope returns null Id. Pre-fix this slipped through. We follow
        // the D.6 pattern and refuse loudly with a -1 placeholder UserId
        // in the exception (operator can correlate via timestamp + path).
        var spec = CreateSpec(headerValue: "5", currentUserId: null, isInternal: false, isMemberOfRequestedSpace: false);
        var message = new TestSpaceScopedRequest();
        var context = CreateContext(message);

        var ex = await Should.ThrowAsync<CrossSpaceAccessDeniedException>(
            () => spec.BeforeExecute(context.Object, CancellationToken.None));

        ex.UserId.ShouldBe(-1, customMessage:
            "Null UserId must reject with -1 placeholder (audit-trail signal that identity was unresolved).");
        ex.RequestedSpaceId.ShouldBe(5);
    }

    [Fact]
    public async Task NonSpaceScopedMessage_PassesThrough()
    {
        // Messages that don't implement ISpaceScoped don't carry a SpaceId
        // contract, so the membership gate has nothing to check.
        var spec = CreateSpec(headerValue: "99", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
        var message = new TestNonSpaceScopedRequest();
        var context = CreateContext(message);

        await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
    }

    // ── Three-mode enforcement (Rule 11 — pre-push audit defence) ───────────

    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        // Operators reference this name in emergency-rollback runbooks.
        // Renaming breaks every documented mitigation procedure.
        Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar
            .ShouldBe("SQUID_SPACE_MEMBERSHIP_ENFORCEMENT");
    }

    [Fact]
    public async Task DefaultMode_NoEnvVar_IsStrict_RejectsPrivesc()
    {
        // Per CLAUDE.md Rule 11: privesc guards default to STRICT, NOT Warn.
        // Default-Warn would silently leave the vulnerability open. Pin this
        // contract — a regression to default-Warn would re-open D.3.
        var previous = Environment.GetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, null);

        try
        {
            var spec = CreateSpec(headerValue: "99", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
            var message = new TestSpaceScopedRequest();
            var context = CreateContext(message);

            await Should.ThrowAsync<CrossSpaceAccessDeniedException>(
                () => spec.BeforeExecute(context.Object, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, previous);
        }
    }

    [Fact]
    public async Task WarnMode_PrivescAttempt_AllowsButWouldLog()
    {
        // Warn mode is the rolling-upgrade migration knob. Operators with
        // historical API-key users lacking TeamMember rows flip to warn for
        // one window to identify violations without breaking deploys, fix
        // the integrations, then flip back to strict.
        var previous = Environment.GetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, "warn");

        try
        {
            var spec = CreateSpec(headerValue: "99", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
            var message = new TestSpaceScopedRequest();
            var context = CreateContext(message);

            await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, previous);
        }
    }

    [Fact]
    public async Task OffMode_PrivescAttempt_SilentlyAllows()
    {
        // Off mode is the emergency-rollback knob. No log, no throw —
        // identical to pre-Phase-10.3 behaviour. Operators use this ONLY
        // during incident response when the membership data store has a
        // bug and no users would pass the check.
        var previous = Environment.GetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, "off");

        try
        {
            var spec = CreateSpec(headerValue: "99", currentUserId: 1, isInternal: false, isMemberOfRequestedSpace: false);
            var message = new TestSpaceScopedRequest();
            var context = CreateContext(message);

            await Should.NotThrowAsync(() => spec.BeforeExecute(context.Object, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, previous);
        }
    }

    [Fact]
    public async Task NullUserId_RejectsRegardlessOfMode()
    {
        // Defence-in-depth: null UserId means identity isn't resolved — that's
        // unambiguously a bug, not a permission decision. Even Warn mode
        // throws here (per the comment in production code).
        var previous = Environment.GetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar);
        Environment.SetEnvironmentVariable(
            Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, "warn");

        try
        {
            var spec = CreateSpec(headerValue: "5", currentUserId: null, isInternal: false, isMemberOfRequestedSpace: false);
            var message = new TestSpaceScopedRequest();
            var context = CreateContext(message);

            await Should.ThrowAsync<CrossSpaceAccessDeniedException>(
                () => spec.BeforeExecute(context.Object, CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                Squid.Core.Middlewares.SpaceScope.SpaceMembershipSpecification<IContext<IMessage>>.EnforcementEnvVar, previous);
        }
    }

    // ========== Helpers ==========

    private static SpaceMembershipSpecification<IContext<IMessage>> CreateSpec(
        string headerValue, int? currentUserId, bool isInternal, bool isMemberOfRequestedSpace)
    {
        var httpContext = new DefaultHttpContext();
        if (headerValue != null)
            httpContext.Request.Headers["X-Space-Id"] = headerValue;

        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(x => x.HttpContext).Returns(httpContext);

        var resolver = new Mock<ISpaceMembershipResolver>();
        resolver.Setup(r => r.IsMemberAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(isMemberOfRequestedSpace);

        var currentUser = new Mock<ICurrentUser>();
        currentUser.Setup(u => u.Id).Returns(currentUserId);
        currentUser.Setup(u => u.IsInternal).Returns(isInternal);

        return new SpaceMembershipSpecification<IContext<IMessage>>(
            accessor.Object, resolver.Object, currentUser.Object);
    }

    private static Mock<IContext<IMessage>> CreateContext(IMessage message)
    {
        var context = new Mock<IContext<IMessage>>();
        context.Setup(c => c.Message).Returns(message);
        return context;
    }

    private class TestSpaceScopedRequest : IRequest, ISpaceScoped
    {
        public int? SpaceId { get; set; }
    }

    private class TestNonSpaceScopedRequest : IRequest
    {
    }
}
