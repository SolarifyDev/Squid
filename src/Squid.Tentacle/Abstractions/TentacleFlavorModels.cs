using Squid.Tentacle.Configuration;

namespace Squid.Tentacle.Abstractions;

public sealed class TentacleIdentity
{
    public TentacleIdentity(string subscriptionId, string thumbprint)
    {
        SubscriptionId = subscriptionId;
        Thumbprint = thumbprint;
    }

    public string SubscriptionId { get; }

    public string Thumbprint { get; }
}

public sealed class TentacleRegistration
{
    public int MachineId { get; init; }

    public string ServerThumbprint { get; init; } = string.Empty;

    public string SubscriptionUri { get; init; } = string.Empty;
}

public sealed class TentacleFlavorContext
{
    public required TentacleSettings TentacleSettings { get; init; }

    public required KubernetesSettings KubernetesSettings { get; init; }
}

public sealed class TentacleFlavorRuntime
{
    public required ITentacleRegistrar Registrar { get; init; }

    public required ITentacleScriptBackend ScriptBackend { get; init; }

    public IReadOnlyList<ITentacleBackgroundTask> BackgroundTasks { get; init; } =
        Array.Empty<ITentacleBackgroundTask>();

    public IReadOnlyList<ITentacleStartupHook> StartupHooks { get; init; } =
        Array.Empty<ITentacleStartupHook>();
}
