using System.Collections.Frozen;
using System.Reflection;

namespace Squid.Message.Enums;

public static class PermissionExtensions
{
    private static readonly FrozenDictionary<Permission, PermissionScope> ScopeCache = BuildScopeCache();

    public static PermissionScope GetScope(this Permission permission)
    {
        return ScopeCache.TryGetValue(permission, out var scope)
            ? scope
            : throw new InvalidOperationException($"Permission {permission} is missing [PermissionScope] attribute.");
    }

    public static bool CanApplyAtSpaceLevel(this Permission permission)
    {
        var scope = permission.GetScope();
        return scope is PermissionScope.SpaceOnly or PermissionScope.Mixed;
    }

    public static bool CanApplyAtSystemLevel(this Permission permission)
    {
        var scope = permission.GetScope();
        return scope is PermissionScope.SystemOnly or PermissionScope.Mixed;
    }

    public static (bool CanApplyAtSpaceLevel, bool CanApplyAtSystemLevel) GetRoleScope(this IEnumerable<Permission> permissions)
    {
        var hasSpaceOnly = false;
        var hasSystemOnly = false;
        var hasMixed = false;

        foreach (var p in permissions)
        {
            switch (p.GetScope())
            {
                case PermissionScope.SpaceOnly: hasSpaceOnly = true; break;
                case PermissionScope.SystemOnly: hasSystemOnly = true; break;
                case PermissionScope.Mixed: hasMixed = true; break;
            }
        }

        var onlyMixed = !hasSpaceOnly && !hasSystemOnly && hasMixed;

        return (hasSpaceOnly || onlyMixed, hasSystemOnly || onlyMixed);
    }

    private static FrozenDictionary<Permission, PermissionScope> BuildScopeCache()
    {
        var dict = new Dictionary<Permission, PermissionScope>();

        foreach (var field in typeof(Permission).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var attr = field.GetCustomAttribute<PermissionScopeAttribute>();
            if (attr != null && Enum.TryParse<Permission>(field.Name, out var perm))
                dict[perm] = attr.Scope;
        }

        return dict.ToFrozenDictionary();
    }
}
