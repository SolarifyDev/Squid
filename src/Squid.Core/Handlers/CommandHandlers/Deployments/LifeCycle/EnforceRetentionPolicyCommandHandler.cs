using Squid.Core.Services.Deployments.LifeCycle;
using Squid.Message.Commands.Deployments.LifeCycle;

namespace Squid.Core.Handlers.CommandHandlers.Deployments.LifeCycle;

public class EnforceRetentionPolicyCommandHandler(IRetentionPolicyEnforcer retentionPolicyEnforcer)
    : ICommandHandler<EnforceRetentionPolicyCommand, EnforceRetentionPolicyResponse>
{
    public async Task<EnforceRetentionPolicyResponse> Handle(IReceiveContext<EnforceRetentionPolicyCommand> context, CancellationToken cancellationToken)
    {
        var projectCount = await retentionPolicyEnforcer.EnforceRetentionForAllProjectsAsync(cancellationToken).ConfigureAwait(false);

        return new EnforceRetentionPolicyResponse
        {
            Data = new EnforceRetentionPolicyResponseData
            {
                ProjectCount = projectCount
            }
        };
    }
}
