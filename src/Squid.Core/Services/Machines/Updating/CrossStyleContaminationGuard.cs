using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Updating;

/// <summary>
/// Shared helper: strategies declare their <c>ownedFieldNames</c>, this
/// helper walks <see cref="UpdateCommandFieldInventory"/> and throws the
/// first cross-style contamination it finds. Round-6 R6-A defence in
/// depth — if any strategy forgets to enforce its field boundary, this
/// shared check is the safety net.
/// </summary>
internal static class CrossStyleContaminationGuard
{
    public static void ThrowIfCommandTouchesNonOwnedFields(
        int machineId,
        string styleName,
        IReadOnlySet<string> ownedFieldNames,
        UpdateMachineCommand command)
    {
        foreach (var field in UpdateCommandFieldInventory.EnumerateStyleFields(command))
        {
            if (!field.IsSet) continue;

            if (ownedFieldNames.Contains(field.Name)) continue;

            throw new MachineEndpointUpdateNotApplicableException(
                machineId,
                styleName,
                field.Name,
                acceptedForStyles: DescribeOwnership(field.Name));
        }
    }

    /// <summary>
    /// Given a field name, return the human-readable list of styles it
    /// applies to — used in the exception's message so the operator knows
    /// exactly where that field belongs.
    /// </summary>
    private static string DescribeOwnership(string fieldName) => fieldName switch
    {
        nameof(UpdateMachineCommand.ClusterUrl) or
        nameof(UpdateMachineCommand.SkipTlsVerification) or
        nameof(UpdateMachineCommand.ProviderType) or
        nameof(UpdateMachineCommand.ProviderConfig)
            => "KubernetesApi",

        nameof(UpdateMachineCommand.Namespace)
            => "KubernetesApi, KubernetesAgent",

        nameof(UpdateMachineCommand.ReleaseName) or
        nameof(UpdateMachineCommand.HelmNamespace) or
        nameof(UpdateMachineCommand.ChartRef)
            => "KubernetesAgent",

        nameof(UpdateMachineCommand.SubscriptionId)
            => "TentaclePolling, KubernetesAgent",

        nameof(UpdateMachineCommand.Thumbprint)
            => "TentaclePolling, TentacleListening, KubernetesAgent",

        nameof(UpdateMachineCommand.Uri) or
        nameof(UpdateMachineCommand.ProxyId)
            => "TentacleListening",

        nameof(UpdateMachineCommand.BaseUrl) or
        nameof(UpdateMachineCommand.InlineGatewayToken) or
        nameof(UpdateMachineCommand.InlineHooksToken)
            => "OpenClaw",

        nameof(UpdateMachineCommand.Host) or
        nameof(UpdateMachineCommand.Port) or
        nameof(UpdateMachineCommand.Fingerprint) or
        nameof(UpdateMachineCommand.RemoteWorkingDirectory) or
        nameof(UpdateMachineCommand.ProxyType) or
        nameof(UpdateMachineCommand.ProxyHost) or
        nameof(UpdateMachineCommand.ProxyPort) or
        nameof(UpdateMachineCommand.ProxyUsername) or
        nameof(UpdateMachineCommand.ProxyPassword)
            => "Ssh",

        nameof(UpdateMachineCommand.ResourceReferences)
            => "KubernetesApi, OpenClaw, Ssh",

        _ => "(unknown)"
    };
}
