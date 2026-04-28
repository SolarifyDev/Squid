using System.Runtime.ExceptionServices;
using Mediator.Net.Pipeline;
using Microsoft.AspNetCore.Http;
using Squid.Core.Services.Identity;

namespace Squid.Core.Middlewares.SpaceScope;

/// <summary>
/// P0-Phase10.3 (audit D.3 / H-19) — gates the X-Space-Id HTTP header against
/// the requesting user's actual team membership BEFORE
/// <see cref="SpaceIdInjectionSpecification{TContext}"/> trusts the header.
///
/// <para><b>The privesc vector pre-Phase-10.3</b>: a user with Team membership
/// in Space-1 sends a command, with HTTP header <c>X-Space-Id: 2</c>.
/// SpaceIdInjectionSpecification trusted the header verbatim and injected
/// SpaceId=2 into the command body. The user could then read/mutate Space-2
/// resources despite having no membership — the only TRUE cross-space
/// privesc surface in the audit's findings (D.3 / H-19).</para>
///
/// <para><b>Order in pipeline</b>: this spec runs BEFORE
/// <see cref="SpaceIdInjectionSpecification{TContext}"/>. If the user isn't
/// a member of the requested space, we throw before any injection happens.
/// Internal users (Hangfire, system tasks) bypass entirely — they're
/// trusted (Phase-7 D.6 IsInternal pattern).</para>
///
/// <para>Missing or empty header is allowed through — the injection spec
/// then falls back to body-supplied SpaceId or null. We're guarding only
/// the header-trust path.</para>
/// </summary>
public sealed class SpaceMembershipSpecification<TContext> : IPipeSpecification<TContext>
    where TContext : IContext<IMessage>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ISpaceMembershipResolver _membershipResolver;
    private readonly ICurrentUser _currentUser;

    public SpaceMembershipSpecification(
        IHttpContextAccessor httpContextAccessor,
        ISpaceMembershipResolver membershipResolver,
        ICurrentUser currentUser)
    {
        _httpContextAccessor = httpContextAccessor;
        _membershipResolver = membershipResolver;
        _currentUser = currentUser;
    }

    public bool ShouldExecute(TContext context, CancellationToken cancellationToken) => true;

    public async Task BeforeExecute(TContext context, CancellationToken cancellationToken)
    {
        if (context.Message is not Message.Contracts.ISpaceScoped) return;

        // Internal users (Hangfire / system tasks) bypass the gate. Same
        // bypass pattern as AuthorizationSpecification — Phase-7 D.6's
        // IsInternal property is the canonical "trusted internal" signal,
        // explicitly NOT keyed off the Id-equals-8888 heuristic.
        if (_currentUser.IsInternal) return;

        if (!TryReadHeaderSpaceId(out var headerSpaceId)) return;  // no header → fall through

        // The user MUST have a resolvable identity to claim a space.
        // Pre-Phase-7-D.6 a null Id silently slipped through; we follow
        // the Phase-7 fail-closed pattern and reject loudly.
        if (_currentUser.Id == null)
            throw new CrossSpaceAccessDeniedException(userId: -1, requestedSpaceId: headerSpaceId);

        var isMember = await _membershipResolver
            .IsMemberAsync(_currentUser.Id.Value, headerSpaceId, cancellationToken)
            .ConfigureAwait(false);

        if (!isMember)
            throw new CrossSpaceAccessDeniedException(_currentUser.Id.Value, headerSpaceId);
    }

    private bool TryReadHeaderSpaceId(out int spaceId)
    {
        spaceId = 0;
        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers["X-Space-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue)) return false;
        return int.TryParse(headerValue, out spaceId);
    }

    public Task Execute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AfterExecute(TContext context, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task OnException(Exception ex, TContext context)
    {
        ExceptionDispatchInfo.Capture(ex).Throw();
        return Task.CompletedTask;
    }
}
