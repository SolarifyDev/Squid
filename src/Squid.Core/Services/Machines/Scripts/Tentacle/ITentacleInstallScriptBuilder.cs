using Squid.Message.Commands.Machine;

namespace Squid.Core.Services.Machines.Scripts.Tentacle;

/// <summary>
/// Generates one platform/method-specific install script variant for a Tentacle.
/// Autowired via <see cref="IScopedDependency"/>: the service layer enumerates all
/// implementations, optionally filters by <see cref="TentacleInstallContext.Command.OperatingSystem"/>,
/// and returns the resulting list in <see cref="GenerateTentacleInstallScriptData.Scripts"/>.
/// Adding a new install method (Windows MSI, Chocolatey, macOS Homebrew…) means adding
/// a new implementation — no change to contract or service code.
/// </summary>
public interface ITentacleInstallScriptBuilder : IScopedDependency
{
    string Id { get; }
    string Label { get; }
    string OperatingSystem { get; }
    string InstallationMethod { get; }
    string ScriptType { get; }
    bool IsRecommended { get; }

    TentacleInstallScript Build(TentacleInstallContext context);
}
