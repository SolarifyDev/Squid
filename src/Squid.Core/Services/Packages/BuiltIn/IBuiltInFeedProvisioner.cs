namespace Squid.Core.Services.Packages.BuiltIn;

public interface IBuiltInFeedProvisioner : IScopedDependency
{
    /// <summary>
    /// Gives the space its built-in feed unless it has one. Safe to call again, and safe when two callers race. Throws <c>BuiltInFeedConflictException</c>
    /// when a feed of the built-in type exists that is not a built-in feed (it has a URI): it is never adopted.
    /// </summary>
    Task EnsureAsync(int spaceId, CancellationToken ct);

    /// <summary>Does <see cref="EnsureAsync"/> for every space and returns how many feeds it created.</summary>
    Task<int> EnsureForAllSpacesAsync(CancellationToken ct);
}
