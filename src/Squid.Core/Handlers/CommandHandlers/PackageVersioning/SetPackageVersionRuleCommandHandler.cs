using Squid.Core.Services.Packages.Versioning;
using Squid.Message.Commands.PackageVersioning;

namespace Squid.Core.Handlers.CommandHandlers.PackageVersioning;

public class SetPackageVersionRuleCommandHandler(IPackageVersionRuleService rules) : ICommandHandler<SetPackageVersionRuleCommand, SetPackageVersionRuleResponse>
{
    public async Task<SetPackageVersionRuleResponse> Handle(IReceiveContext<SetPackageVersionRuleCommand> context, CancellationToken cancellationToken)
    {
        var rule = await rules.SetRuleAsync(context.Message, cancellationToken).ConfigureAwait(false);

        return new SetPackageVersionRuleResponse { Data = rule };
    }
}
