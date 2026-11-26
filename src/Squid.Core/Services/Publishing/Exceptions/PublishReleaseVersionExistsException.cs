namespace Squid.Core.Services.Publishing.Exceptions;

/// <summary>The release for this version exists, but the process changed after it was created, so deploying it would run the old process.</summary>
public sealed class PublishReleaseVersionExistsException(int projectId, string version) : InvalidOperationException($"Project {projectId} already has a release {version}, made before the recipe changed the process. Delete that release, or publish a new version.")
{
    public int ProjectId { get; } = projectId;

    public string Version { get; } = version;
}
