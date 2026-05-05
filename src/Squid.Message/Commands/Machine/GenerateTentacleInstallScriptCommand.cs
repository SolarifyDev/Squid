using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Requests.Machines;
using Squid.Message.Response;

namespace Squid.Message.Commands.Machine;

[RequiresPermission(Permission.MachineCreate)]
public class GenerateTentacleInstallScriptCommand : ICommand, ISpaceScoped
{
    // Target machine metadata
    public string MachineName { get; set; }
    public List<string> Environments { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int SpaceId { get; set; } = 1;
    int? ISpaceScoped.SpaceId => SpaceId;

    // Server connection
    public string ServerUrl { get; set; }
    public string ServerCommsUrl { get; set; }

    // Communication mode
    public string CommunicationMode { get; set; } = "Listening";
    public string ListeningHostName { get; set; }
    public int ListeningPort { get; set; } = 10933;

    // Filter: only generate scripts for this OS. Null = return all.
    public string OperatingSystem { get; set; }

    // Method-specific overrides
    public string DockerImage { get; set; }
}

public class GenerateTentacleInstallScriptResponse : SquidResponse<GenerateTentacleInstallScriptData>
{
}

public class GenerateTentacleInstallScriptData
{
    public string ServerThumbprint { get; set; }
    public List<TentacleInstallScript> Scripts { get; set; } = [];

    /// <summary>
    /// Diagnostic result of probing the configured polling URL at
    /// script-generation time. Lets the UI display an actionable warning when
    /// DNS / SLB / firewall is not yet configured correctly, instead of
    /// silently handing out a script that will fail with cryptic EOF errors
    /// once a real Tentacle tries to poll.
    /// </summary>
    public TentacleCommsProbeInfo CommsUrlProbe { get; set; }

    /// <summary>
    /// Per-OS / per-architecture archive download URLs corresponding to the
    /// scripts above. Bundled in the same response so the FE wizard can
    /// render BOTH UX paths in a single step:
    /// <list type="bullet">
    ///   <item>"Paste this script" — uses <see cref="Scripts"/></item>
    ///   <item>"Or download the installer manually" — uses <see cref="Downloads"/></item>
    /// </list>
    /// Mirrors Octopus's Tentacle wizard which surfaces an MSI download menu
    /// alongside the auto-install option. Filtered by
    /// <see cref="GenerateTentacleInstallScriptCommand.OperatingSystem"/>.
    /// Bit-identical to the standalone
    /// <c>GET /api/machines/tentacle-downloads</c> response — both call the
    /// same <c>TentacleDownloadCatalog</c>, so a download URL change here
    /// propagates uniformly to both surfaces.
    /// </summary>
    public List<TentacleDownloadDto> Downloads { get; set; } = [];
}

public class TentacleInstallScript
{
    public string Id { get; set; }
    public string Label { get; set; }
    public string OperatingSystem { get; set; }
    public string InstallationMethod { get; set; }
    public string ScriptType { get; set; }
    public string Content { get; set; }
    public bool IsRecommended { get; set; }
}

/// <summary>
/// Transport DTO mirroring the server-side <c>TentacleCommsProbeResult</c>.
/// Shipped to the UI alongside the install scripts so operators see network /
/// SLB misconfigurations at script-generation time instead of at
/// first-Tentacle-handshake time.
/// </summary>
public class TentacleCommsProbeInfo
{
    public bool Reachable { get; set; }
    public bool Skipped { get; set; }
    public string ObservedThumbprint { get; set; } = string.Empty;
    public bool ThumbprintMatches { get; set; }
    public string Detail { get; set; } = string.Empty;
}
