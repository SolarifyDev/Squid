namespace Squid.Core.Services.Machines.Exceptions;

/// <summary>
/// Thrown when a machine-targeting operation (upgrade, health check,
/// connection-status, etc.) is asked to act on a <c>MachineId</c> that does
/// not exist in the database. Mapped to HTTP 404 by the
/// <c>GlobalExceptionFilter</c> so the client can distinguish "you targeted
/// a deleted/never-existed machine" from "the machine exists but the
/// operation failed".
/// </summary>
public sealed class MachineNotFoundException(int machineId)
    : InvalidOperationException($"Machine with id {machineId} not found")
{
    public int MachineId { get; } = machineId;
}
