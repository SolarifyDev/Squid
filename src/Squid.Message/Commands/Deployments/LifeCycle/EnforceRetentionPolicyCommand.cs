using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.LifeCycle;

[RequiresPermission(Permission.LifecycleEdit)]
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
