namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>The project has to be created and the space has no project group or lifecycle to put it in, and the request named none.</summary>
public sealed class PublishDefaultsMissingException(string what, int spaceId) : InvalidOperationException($"Space {spaceId} has no {what}, and the request did not name one. Create one, or name it in the request.")
{
    public string What { get; } = what;

    public int SpaceId { get; } = spaceId;
}
