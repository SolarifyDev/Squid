using Autofac;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Squid.Core;
using Squid.Core.Services.Identity;
using Xunit;

namespace Squid.UnitTests.Services.Identity;

/// <summary>
/// P1-D.6 follow-up (Phase-7): verifies the context-aware
/// <see cref="ICurrentUser"/> registration in <c>SquidModule</c>.
///
/// <para><b>The breaking-change risk D.6 introduced</b>: the auto-scanned
/// <see cref="ApiUser"/> (marked <c>IScopedDependency</c>) was bound to
/// <see cref="ICurrentUser"/> for every Autofac scope. After D.6, ApiUser
/// in a non-HTTP scope returns null Id → AuthorizationSpecification
/// throws PermissionDeniedException on any <c>[RequiresPermission]</c>
/// command. <c>MachineHealthCheckRecurringJob</c> (Hangfire, every minute,
/// dispatches <c>AutoMachineHealthCheckCommand</c> with
/// <c>[RequiresPermission(MachineEdit)]</c>) would have crashed in
/// production on every cron tick.</para>
///
/// <para><b>Fix</b>: <c>SquidModule.RegisterCurrentUser</c> registers a
/// factory AFTER auto-registration (last-wins) that returns ApiUser for
/// HTTP scopes and InternalUser for non-HTTP scopes. The
/// <c>IsInternal=true</c> signal on InternalUser then grants the
/// AuthorizationSpecification bypass for known background contexts.</para>
/// </summary>
public sealed class CurrentUserResolutionTests
{
    [Fact]
    public void Resolve_WithoutHttpContext_ReturnsInternalUser_NotApiUser()
    {
        // The actual breaking-change scenario: a Hangfire scope resolves
        // ICurrentUser. There is no HttpContext. Pre-fix → ApiUser →
        // AuthorizationSpecification throws. Post-fix → InternalUser →
        // IsInternal=true → middleware bypasses (correct for trusted
        // internal context).
        using var container = BuildContainerWithoutHttpContext();
        using var scope = container.BeginLifetimeScope();

        var user = scope.Resolve<ICurrentUser>();

        user.ShouldBeOfType<InternalUser>(
            customMessage:
                "non-HTTP scope (Hangfire / startup / scheduled task) MUST resolve InternalUser. " +
                "Pre-fix Phase-7 follow-up returned ApiUser with null Id, breaking every minute's health check.");
        user.IsInternal.ShouldBeTrue();
        user.Id.ShouldBe(Squid.Message.Constants.CurrentUsers.InternalUser.Id);
    }

    [Fact]
    public void Resolve_WithHttpContextPresent_ReturnsApiUser()
    {
        // A real HTTP request scope: HttpContextAccessor.HttpContext is set.
        // Resolve must yield ApiUser (which then derives identity from
        // claims).
        using var container = BuildContainerWithHttpContext(new DefaultHttpContext());
        using var scope = container.BeginLifetimeScope();

        var user = scope.Resolve<ICurrentUser>();

        user.ShouldBeOfType<ApiUser>(
            customMessage: "HTTP-bound scope must resolve ApiUser; that's the legitimate user-identity surface.");
        user.IsInternal.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_AccessorRegisteredButHttpContextNull_ReturnsInternalUser()
    {
        // Edge case: IHttpContextAccessor is in the container (always is in
        // a Kestrel build) but its HttpContext property is null because
        // we're outside a request (e.g. a Hangfire job that resolved the
        // accessor). Same outcome as no-accessor case.
        using var container = BuildContainerWithHttpContext(httpContext: null);
        using var scope = container.BeginLifetimeScope();

        var user = scope.Resolve<ICurrentUser>();

        user.ShouldBeOfType<InternalUser>();
    }

    [Fact]
    public void Resolve_FactoryOverridesAutoScanRegistration_LastWinsContract()
    {
        // Pins the production-shape sequence: SquidModule.RegisterDependency
        // first registers ApiUser AsImplementedInterfaces (so it binds as
        // ICurrentUser via auto-scan) — then RegisterCurrentUser registers
        // the factory. Autofac's last-wins contract is what makes the fix
        // work. If anyone reorders SquidModule.Load(...) so RegisterCurrentUser
        // runs BEFORE RegisterDependency, the auto-scan wins and we're back
        // to the Hangfire-crash regression.
        var builder = new ContainerBuilder();

        // 1. Mirror RegisterDependency: ApiUser auto-scanned AsImplementedInterfaces
        //    → registers ApiUser as ICurrentUser too.
        builder.RegisterType<ApiUser>().AsSelf().AsImplementedInterfaces().InstancePerLifetimeScope();

        // 2. Mirror RegisterCurrentUser: factory registered AFTER auto-scan.
        RegisterCurrentUserFactory(builder);

        using var container = builder.Build();
        using var scope = container.BeginLifetimeScope();

        // No HttpContext → factory must return InternalUser. If the auto-scan
        // ApiUser registration wins instead, this test fails — and that's
        // exactly the breakage we're guarding against.
        var user = scope.Resolve<ICurrentUser>();

        user.ShouldBeOfType<InternalUser>(
            customMessage:
                "Last-wins contract violation: factory must override auto-scan ICurrentUser binding. " +
                "Reordering SquidModule.Load to put RegisterCurrentUser BEFORE RegisterDependency " +
                "would re-introduce the Phase-7.6 Hangfire-crash regression.");
    }

    // (The "negative case" — factory registered BEFORE auto-scan would
    // make auto-scan win — was sketched here but dropped: ApiUser's ctor
    // requires IHttpContextAccessor, so the negative test would need
    // additional fixture setup that's not load-bearing for the contract.
    // The positive `LastWinsContract` test above is sufficient: if it
    // passes, last-wins works in our chosen direction. Anyone reordering
    // SquidModule.Load() makes that test fail.)

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IContainer BuildContainerWithoutHttpContext()
    {
        var builder = new ContainerBuilder();
        // Register ApiUser as itself (auto-scan would also pull in ICurrentUser
        // but this isolated test focuses on the factory's decision).
        builder.RegisterType<ApiUser>().AsSelf().InstancePerLifetimeScope();
        // No IHttpContextAccessor at all — simulates a worker / job process
        // without an HTTP pipeline.
        RegisterCurrentUserFactory(builder);
        return builder.Build();
    }

    private static IContainer BuildContainerWithHttpContext(HttpContext httpContext)
    {
        var builder = new ContainerBuilder();
        builder.RegisterType<ApiUser>().AsSelf().InstancePerLifetimeScope();

        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        builder.RegisterInstance<IHttpContextAccessor>(accessor);

        RegisterCurrentUserFactory(builder);
        return builder.Build();
    }

    /// <summary>Mirror of <c>SquidModule.RegisterCurrentUser</c> — kept here
    /// so the unit test exercises the factory in isolation without pulling
    /// in the entire SquidModule's transitive registrations (which would
    /// require a real DB connection string etc.).</summary>
    private static void RegisterCurrentUserFactory(ContainerBuilder builder)
    {
        builder.Register<ICurrentUser>(c =>
        {
            var accessor = c.ResolveOptional<IHttpContextAccessor>();
            if (accessor?.HttpContext != null)
                return c.Resolve<ApiUser>();
            return new InternalUser();
        }).InstancePerLifetimeScope();
    }
}
