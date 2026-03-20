namespace Squid.Message.Models.Authorization;

public class ResourceScope
{
    public bool IsUnrestricted { get; init; }
    public bool IsProjectUnrestricted { get; init; }
    public bool IsEnvironmentUnrestricted { get; init; }
    public bool IsProjectGroupUnrestricted { get; init; }
    public HashSet<int> ProjectIds { get; init; } = new();
    public HashSet<int> EnvironmentIds { get; init; } = new();
    public HashSet<int> ProjectGroupIds { get; init; } = new();

    public static ResourceScope Unrestricted() => new()
    {
        IsUnrestricted = true,
        IsProjectUnrestricted = true,
        IsEnvironmentUnrestricted = true,
        IsProjectGroupUnrestricted = true,
    };

    public static ResourceScope None() => new()
    {
        IsUnrestricted = false,
        IsProjectUnrestricted = false,
        IsEnvironmentUnrestricted = false,
        IsProjectGroupUnrestricted = false,
    };
}
