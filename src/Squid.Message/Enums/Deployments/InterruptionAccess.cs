namespace Squid.Message.Enums.Deployments;

/// <summary>How a user is allowed to act on (submit / take responsibility for) an interruption.</summary>
public enum InterruptionAccess
{
    Denied = 0,

    /// <summary>The interruption has no responsible teams, so anyone allowed to submit interruptions may act.</summary>
    Unrestricted = 1,

    /// <summary>The user is a member of one of the interruption's responsible teams.</summary>
    TeamMember = 2,

    /// <summary>Not a team member; allowed only because the user holds InterruptionSubmitOverride (or AdministerSystem) for the interruption's space.</summary>
    AdministratorOverride = 3
}
