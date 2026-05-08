using Microsoft.Extensions.Configuration;
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

    public required IConfiguration Configuration { get; init; }

    /// <summary>
    /// When true, the flavor's registrar resolution MUST bypass the
    /// "already registered (Registered=true) → NoOpRegistrar" skip path
    /// and run the actual register HTTP call.
    ///
    /// <para>Set by <c>RegisterCommand.ExecuteAsync</c> when the
    /// operator passes <c>--force</c>. The skip path is right for the
    /// <c>run</c> command (systemd restart shouldn't re-register on
    /// every boot) but wrong for explicit operator-invoked
    /// <c>register --force</c> after role/environment/api-key changes
    /// — without this flag, those updates would silently never reach
    /// the server.</para>
    ///
    /// <para>Defaults to <c>false</c> for backward compat: existing
    /// callers (run command, in-UI upgrade flow) keep the same skip
    /// behaviour.</para>
    /// </summary>
    public bool ForceRegistration { get; init; }
}

public enum TentacleCommunicationMode
{
    Polling,
    Listening
}

public sealed class TentacleFlavorRuntime
{
    public required ITentacleRegistrar Registrar { get; init; }

    public required ITentacleScriptBackend ScriptBackend { get; init; }

    public TentacleCommunicationMode CommunicationMode { get; init; } = TentacleCommunicationMode.Polling;

    public int? ListeningPort { get; init; }

    public IReadOnlyList<ITentacleBackgroundTask> BackgroundTasks { get; init; } =
        Array.Empty<ITentacleBackgroundTask>();

    public IReadOnlyList<ITentacleStartupHook> StartupHooks { get; init; } =
        Array.Empty<ITentacleStartupHook>();

    public Func<bool> ReadinessCheck { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new();
}
