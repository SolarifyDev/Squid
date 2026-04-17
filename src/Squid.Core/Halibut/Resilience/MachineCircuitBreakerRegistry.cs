using System.Collections.Concurrent;
using Squid.Core.Settings.Halibut;

namespace Squid.Core.Halibut.Resilience;

public interface IMachineCircuitBreakerRegistry
{
    MachineCircuitBreaker GetOrCreate(int machineId);
}

/// <summary>
/// Registry of per-machine circuit breakers. Breakers are created lazily on
/// first request and cached for the process lifetime. All breakers share the
/// thresholds from <see cref="CircuitBreakerSettings"/>; per-machine overrides
/// can be added in a later iteration without breaking this interface.
/// </summary>
public sealed class MachineCircuitBreakerRegistry : IMachineCircuitBreakerRegistry, ISingletonDependency
{
    private readonly CircuitBreakerSettings _settings;
    private readonly ConcurrentDictionary<int, MachineCircuitBreaker> _breakers = new();

    public MachineCircuitBreakerRegistry(HalibutSetting halibutSetting)
    {
        _settings = halibutSetting?.CircuitBreaker ?? new CircuitBreakerSettings();
    }

    public MachineCircuitBreaker GetOrCreate(int machineId)
    {
        return _breakers.GetOrAdd(machineId, id => new MachineCircuitBreaker(
            id,
            _settings.FailureThreshold,
            TimeSpan.FromSeconds(Math.Max(1, _settings.OpenDurationSeconds))));
    }
}
