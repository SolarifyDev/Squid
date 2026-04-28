namespace Squid.Core.Middlewares.SpaceScope;

/// <summary>
/// P0-Phase10.3 (audit D.3 / H-19) — thrown when a user supplies an
/// <c>X-Space-Id</c> HTTP header for a Space they have NO Team membership in.
///
/// <para>This is the canonical cross-space privilege-escalation defence:
/// the SpaceIdInjectionSpecification trusts the header for command-body
/// injection convenience, but only AFTER this gate confirms the user is
/// actually a member of that space. Operators see a structured 403 with
/// the specific UserId + SpaceId pair so audit logs can identify probing
/// attempts.</para>
/// </summary>
public sealed class CrossSpaceAccessDeniedException : InvalidOperationException
{
    public int UserId { get; }
    public int RequestedSpaceId { get; }

    public CrossSpaceAccessDeniedException(int userId, int requestedSpaceId)
        : base(
            $"User {userId} attempted to access SpaceId={requestedSpaceId} via the X-Space-Id " +
            $"HTTP header, but has no team membership in that space. Request rejected (P0-Phase10.3).")
    {
        UserId = userId;
        RequestedSpaceId = requestedSpaceId;
    }
}
