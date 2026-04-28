using System.Runtime.ExceptionServices;
using Mediator.Net.Pipeline;
using Microsoft.AspNetCore.Http;
using Squid.Core.Services.Identity;
using Squid.Message.Hardening;

namespace Squid.Core.Middlewares.SpaceScope;

/// <summary>
/// P0-Phase10.3 (audit D.3 / H-19) — gates the X-Space-Id HTTP header against
/// the requesting user's actual team membership BEFORE
/// <see cref="SpaceIdInjectionSpecification{TContext}"/> trusts the header.
///
/// <para><b>The privesc vector pre-Phase-10.3</b>: a user with Team membership
/// in Space-1 sends a command, with HTTP header <c>X-Space-Id: 2</c>.
/// SpaceIdInjectionSpecification trusted the header verbatim and injected
/// SpaceId=2 into the command body. The user could read/mutate Space-2
/// resources despite having no membership — the only TRUE cross-space
/// privesc surface in the audit's findings (D.3 / H-19).</para>
///
/// <para><b>Order in pipeline</b>: this spec runs BEFORE
/// <see cref="SpaceIdInjectionSpecification{TContext}"/>. If the user isn't
/// a member of the requested space, we throw before any injection happens.
/// Internal users (Hangfire, system tasks) bypass entirely — they're
/// trusted (Phase-7 D.6 IsInternal pattern).</para>
///
/// <para><b>Three-mode enforcement</b> (CLAUDE.md Rule 11): per the project
/// pattern for hardening checks, this gate exposes
/// <see cref="EnforcementEnvVar"/> with three modes:
/// <list type="bullet">
///   <item><c>off</c> — silent allow (operator emergency rollback)</item>
///   <item><c>warn</c> — allow + log structured warning naming the
///         (UserId, RequestedSpaceId) pair (rolling-upgrade migration window)</item>
///   <item><c>strict</c> ← <b>default</b> — reject with
///         <see cref="CrossSpaceAccessDeniedException"/> (production posture)</item>
/// </list>
/// Per CLAUDE.md Rule 11, privesc guards default to STRICT (NOT Warn) because
/// "default-Warn would silently leave the vulnerability open". Operators with
/// historical API-key integrations that lack TeamMember rows can flip to
/// <c>warn</c> for ONE migration window, observe the warnings, fix the
/// affected integrations, then flip back to <c>strict</c>.</para>
///
/// <para>Missing or empty header is allowed through — the injection spec
/// then falls back to body-supplied SpaceId or null.</para>
/// </summary>
public sealed class SpaceMembershipSpecification<TContext> : IPipeSpecification<TContext>
    where TContext : IContext<IMessage>
{
    /// <summary>
    /// Operator-tunable enforcement mode. Pinned literal — operator runbooks
    /// reference this exact name. Renaming breaks every emergency-rollback
    /// procedure documented for tenants.
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_SPACE_MEMBERSHIP_ENFORCEMENT";

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

        // Mode resolution — default Strict per Rule 11 for privesc guards.
        // Operators with rolling-upgrade migration concerns can set
        // SQUID_SPACE_MEMBERSHIP_ENFORCEMENT=warn for one window, observe
        // the structured warnings naming the offending UserId+SpaceId pairs,
        // fix the integrations, then flip back to strict.
        var mode = EnforcementModeReader.Read(EnforcementEnvVar, defaultMode: EnforcementMode.Strict);
        if (mode == EnforcementMode.Off) return;

        // The user MUST have a resolvable identity to claim a space.
        // Pre-Phase-7-D.6 a null Id silently slipped through; we follow
        // the Phase-7 fail-closed pattern and reject loudly even in Warn
        // mode — null-Id is unambiguously a bug, not a permission decision.
        if (_currentUser.Id == null)
            throw new CrossSpaceAccessDeniedException(userId: -1, requestedSpaceId: headerSpaceId);

        var isMember = await _membershipResolver
            .IsMemberAsync(_currentUser.Id.Value, headerSpaceId, cancellationToken)
            .ConfigureAwait(false);

        if (isMember) return;

        // Non-member path. Mode-gated.
        switch (mode)
        {
            case EnforcementMode.Warn:
                Serilog.Log.Warning(
                    "[SPACE-MEMBERSHIP] User {UserId} accessing SpaceId={SpaceId} without team " +
                    "membership — allowing under {EnvVar}=warn. This is a Phase-10.3 privesc-gate " +
                    "violation: production should run with strict enforcement. To fix: add the user " +
                    "to a Team in Space {SpaceId}, or set {EnvVar}=strict to reject.",
                    _currentUser.Id.Value, headerSpaceId, EnforcementEnvVar, headerSpaceId);
                return;

            case EnforcementMode.Strict:
                throw new CrossSpaceAccessDeniedException(_currentUser.Id.Value, headerSpaceId);

            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
        }
    }

    private bool TryReadHeaderSpaceId(out int spaceId)
    {
        spaceId = 0;
        var headerValue = _httpContextAccessor?.HttpContext?.Request.Headers["X-Space-Id"].FirstOrDefault();
        if (string.IsNullOrEmpty(headerValue)) return false;

        // <c>int.TryParse</c> accepts zero and negative values. SpaceIds are
        // 1-indexed throughout Squid, so any header value &lt;= 0 is either an
        // operator typo or attacker noise. We pass the parsed value through
        // to the membership resolver UNFILTERED — the resolver returns false
        // (no Team has SpaceId == 0 or negative), the gate then rejects with
        // <see cref="CrossSpaceAccessDeniedException"/>. Fail-closed,
        // correct behaviour. We deliberately do NOT pre-validate &gt; 0 here
        // because that would silently swallow the suspicious header rather
        // than logging the rejection at the membership-check layer.
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
