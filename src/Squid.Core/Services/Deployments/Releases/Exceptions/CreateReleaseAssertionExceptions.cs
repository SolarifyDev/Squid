namespace Squid.Core.Services.Deployments.Releases.Exceptions;

public abstract class CreateReleaseAssertionException : InvalidOperationException
{
    protected CreateReleaseAssertionException(string message) : base(message)
    {
    }
}

public sealed class ReleaseProjectNotFoundException : CreateReleaseAssertionException
{
    public int ProjectId { get; }

    public ReleaseProjectNotFoundException(int projectId)
        : base($"Project {projectId} not found")
    {
        ProjectId = projectId;
    }
}

public sealed class ReleaseChannelNotFoundException : CreateReleaseAssertionException
{
    public int ChannelId { get; }

    public ReleaseChannelNotFoundException(int channelId)
        : base($"Channel {channelId} not found")
    {
        ChannelId = channelId;
    }
}

public sealed class ReleaseChannelProjectMismatchException : CreateReleaseAssertionException
{
    public int ChannelId { get; }

    public int ExpectedProjectId { get; }

    public int ActualProjectId { get; }

    public ReleaseChannelProjectMismatchException(int channelId, int expectedProjectId, int actualProjectId)
        : base($"Channel {channelId} does not belong to project {expectedProjectId}")
    {
        ChannelId = channelId;
        ExpectedProjectId = expectedProjectId;
        ActualProjectId = actualProjectId;
    }
}

public sealed class ReleaseSpaceMismatchException : CreateReleaseAssertionException
{
    public int ProjectId { get; }

    public int ChannelId { get; }

    public int ProjectSpaceId { get; }

    public int ChannelSpaceId { get; }

    public ReleaseSpaceMismatchException(int projectId, int channelId, int projectSpaceId, int channelSpaceId)
        : base($"Project {projectId} and channel {channelId} space mismatch")
    {
        ProjectId = projectId;
        ChannelId = channelId;
        ProjectSpaceId = projectSpaceId;
        ChannelSpaceId = channelSpaceId;
    }
}
