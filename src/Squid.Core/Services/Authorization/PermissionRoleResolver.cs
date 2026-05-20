using Squid.Core.Services.DataSeeding;
using Squid.Message.Enums;

namespace Squid.Core.Services.Authorization;

/// <summary>
/// Maps a denied <see cref="Permission"/> to the built-in roles that grant it,
/// so the server's 403 response body + the Tentacle CLI's error output can
/// give operators an actionable remediation path instead of an opaque
/// "Permission denied" message.
///
/// <para><b>Why a static helper, not a service</b>: built-in role
/// definitions are compile-time constants (<see cref="BuiltInRoleSeeder"/>).
/// They don't change at runtime, so we don't need DI lifetime management —
/// a pure function is enough.</para>
///
/// <para><b>Custom roles</b>: operators can grant <c>MachineCreate</c> to
/// any custom role they create. The "suggested roles" list intentionally
/// surfaces only the BUILT-IN roles that ship with Squid, because those
/// are the ones every install has by default. The error message tells the
/// operator they can also add the permission to their existing custom
/// role — covers the "we have a custom role and don't want to change
/// roles" workflow.</para>
/// </summary>
public static class PermissionRoleResolver
{
    /// <summary>
    /// Returns the names of the built-in roles that grant <paramref name="permission"/>
    /// AND are safe to suggest to a human operator (i.e. <c>IsReservedForSystem == false</c>).
    /// Empty when no operator-assignable role grants it.
    ///
    /// <para>System-reserved roles (e.g. <c>SystemServiceAccount</c>, used by the Tentacle
    /// bootstrap key's owner account) are deliberately filtered out -- suggesting them
    /// would lead an operator to assign a system-reserved role to a human user, defeating
    /// the least-privilege isolation those roles are designed for.</para>
    /// </summary>
    public static IReadOnlyList<string> GetBuiltInRolesGranting(Permission permission)
        => GetBuiltInRolesGranting(permission, includeSystemReserved: false);

    /// <summary>
    /// Lower-level variant that lets the caller choose whether to include system-reserved
    /// roles. Internal callers (seeders, diagnostics) may need the full list; operator-facing
    /// surfaces (403 hint, install-script error message) MUST stick with the filtered default.
    /// </summary>
    public static IReadOnlyList<string> GetBuiltInRolesGranting(Permission permission, bool includeSystemReserved)
    {
        var matches = new List<string>();

        foreach (var role in BuiltInRoles.All)
        {
            if (!role.Permissions.Contains(permission)) continue;
            if (role.IsReservedForSystem && !includeSystemReserved) continue;

            matches.Add(role.Name);
        }

        return matches;
    }

    /// <summary>
    /// Composes a single-paragraph operator-facing hint that names the missing
    /// permission AND lists the built-in roles that grant it. Empty string when
    /// no built-in role grants it (the structured response still carries
    /// <c>MissingPermission</c> + an empty <c>SuggestedRoles</c>, and the
    /// install script's exit-code handler can fall back to its own message).
    /// </summary>
    public static string BuildOperatorHint(Permission permission)
    {
        var roles = GetBuiltInRolesGranting(permission);

        if (roles.Count == 0)
        {
            return $"Missing permission '{permission}'. No built-in role grants it — " +
                   $"add the permission to the API key user's current role, or create a custom role with it.";
        }

        return $"Missing permission '{permission}'. Built-in roles that grant it: " +
               $"{string.Join(", ", roles)}. Assign one of these roles to the API key user, " +
               $"or add '{permission}' to their existing role.";
    }
}
