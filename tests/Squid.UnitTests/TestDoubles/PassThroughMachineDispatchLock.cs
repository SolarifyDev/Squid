using System;
using System.Threading.Tasks;
using Squid.Core.Services.Machines.Locking;

namespace Squid.UnitTests.TestDoubles;

/// <summary>
/// Test double for <see cref="IMachineDispatchLock"/> that simply runs the action with no real
/// distributed lock. Used by deployment-pipeline unit tests that exercise script execution but
/// do not test the per-machine locking itself (that is covered by MachineDispatchLockTests and
/// the runner's pause tests).
/// </summary>
public sealed class PassThroughMachineDispatchLock : IMachineDispatchLock
{
    public static readonly PassThroughMachineDispatchLock Instance = new();

    public Task<T> RunUnderMachineLockAsync<T>(int machineId, Func<Task<T>> action) where T : class => action();
}
