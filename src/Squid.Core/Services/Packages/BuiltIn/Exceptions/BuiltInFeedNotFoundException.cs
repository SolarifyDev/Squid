namespace Squid.Core.Services.Packages.BuiltIn.Exceptions;

public sealed class BuiltInFeedNotFoundException(int spaceId) : InvalidOperationException($"Space {spaceId} has no built-in package feed")
{
    public int SpaceId { get; } = spaceId;
}
