using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

public class EnforceRetentionPolicyCommand : ICommand
{
}

public class EnforceRetentionPolicyResponse : SquidResponse<EnforceRetentionPolicyResponseData>
{
}

public class EnforceRetentionPolicyResponseData
{
    public int ProjectCount { get; set; }
}
