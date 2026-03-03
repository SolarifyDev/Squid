using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.ExternalFeed;

public class TestExternalFeedCommand : ICommand
{
    public int Id { get; set; }
}

public class TestExternalFeedResponse : SquidResponse<TestExternalFeedResponseData>
{
}

public class TestExternalFeedResponseData
{
    public bool Success { get; set; }
    public string Message { get; set; }
}
