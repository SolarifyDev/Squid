using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Machine;

namespace Squid.Core.Services.Machines.Updating;

public sealed class OpenClawUpdateStrategy : MachineUpdateStrategyBase<OpenClawEndpointDto>
{
    protected override string StyleName => nameof(CommunicationStyle.OpenClaw);

    protected override IReadOnlySet<string> OwnedFieldNames { get; } = new HashSet<string>
    {
        nameof(UpdateMachineCommand.BaseUrl),
        nameof(UpdateMachineCommand.InlineGatewayToken),
        nameof(UpdateMachineCommand.InlineHooksToken),
        nameof(UpdateMachineCommand.ResourceReferences),
    };

    protected override void ApplyOwnedFields(OpenClawEndpointDto e, UpdateMachineCommand c)
    {
        e.BaseUrl = c.BaseUrl ?? e.BaseUrl;
        e.InlineGatewayToken = c.InlineGatewayToken ?? e.InlineGatewayToken;
        e.InlineHooksToken = c.InlineHooksToken ?? e.InlineHooksToken;
        e.ResourceReferences = c.ResourceReferences ?? e.ResourceReferences;
    }
}
