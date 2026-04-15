using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// Runtime context passed to <see cref="ITentacleInstallScriptBuilder"/> implementations.
/// Carries the user-supplied command plus values resolved by the server (API key, server thumbprint).
/// </summary>
public sealed class TentacleInstallContext
{
    public GenerateTentacleInstallScriptCommand Command { get; init; }
    public string ApiKey { get; init; }
    public string ServerThumbprint { get; init; }
    public bool IsListening { get; init; }

    public string RolesCsv => string.Join(",", Command.Tags ?? []);
    public string EnvironmentsCsv => string.Join(",", Command.Environments ?? []);
}
