namespace Squid.Core.Services.Machines.Exceptions;

/// <summary>
/// Thrown when <c>UpdateMachineCommand</c> carries a style-specific field
/// that doesn't belong to the target machine's <c>CommunicationStyle</c>
/// (e.g. sending <c>ClusterUrl</c> to a TentaclePolling machine).
///
/// <para>Round-6 R6-A fix: previously the update path treated every
/// non-OpenClaw machine as KubernetesApi, silently re-serialising the
/// endpoint JSON and destroying fields like <c>Thumbprint</c>,
/// <c>SubscriptionId</c>, <c>Uri</c>, <c>Host</c>. This exception is the
/// fail-fast replacement — no mutation happens before it throws. Mapped
/// to HTTP 400 by <c>GlobalExceptionFilter</c>.</para>
/// </summary>
public sealed class MachineEndpointUpdateNotApplicableException : InvalidOperationException
{
    public MachineEndpointUpdateNotApplicableException(
        int machineId,
        string machineStyle,
        string offendingField,
        string acceptedForStyles)
        : base(
            $"Cannot set '{offendingField}' on machine {machineId}: that field only applies to [{acceptedForStyles}] machines, " +
            $"not to a '{machineStyle}' machine. Send the fields belonging to '{machineStyle}' instead, " +
            "or delete+re-register the machine if the underlying style needs to change.")
    {
        MachineId = machineId;
        MachineStyle = machineStyle;
        OffendingField = offendingField;
        AcceptedForStyles = acceptedForStyles;
    }

    public int MachineId { get; }

    public string MachineStyle { get; }

    public string OffendingField { get; }

    public string AcceptedForStyles { get; }
}
