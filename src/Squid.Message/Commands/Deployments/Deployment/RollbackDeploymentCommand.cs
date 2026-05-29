using System.Text.Json.Serialization;
using Squid.Message.Attributes;
using Squid.Message.Enums;
using Squid.Message.Response;

namespace Squid.Message.Commands.Deployments.Deployment;

/// <summary>
/// Roll an environment back to a prior release. A rollback is just a normal
/// deployment of an older release (no special "rollback" flag on the
/// deployment), so this reuses the entire <see cref="CreateDeploymentCommand"/>
/// path once the target release is resolved.
///
/// <para><see cref="ReleaseId"/> is optional: when omitted, the server rolls
/// back to the previously-running successful release for the environment
/// (auto target). When supplied, it must be a release that previously
/// deployed successfully to this environment and differ from the current one
/// — letting an operator jump back to any prior good version, not just one
/// step.</para>
///
/// <para>Carries <see cref="Permission.DeploymentCreate"/> — a rollback IS a
/// deployment.</para>
/// </summary>
[RequiresPermission(Permission.DeploymentCreate)]
public class RollbackDeploymentCommand : ICommand, ISpaceScoped
{
    public int? SpaceId { get; set; }

    public int ProjectId { get; set; }

    public int EnvironmentId { get; set; }

    /// <summary>Optional explicit rollback target. Null = auto-resolve the
    /// previous successful release for the environment.</summary>
    public int? ReleaseId { get; set; }

    public string Name { get; set; }

    public string Comments { get; set; }

    public bool ForcePackageDownload { get; set; }

    public bool ForcePackageRedeployment { get; set; }

    public bool UseGuidedFailure { get; set; }

    public DateTimeOffset? QueueTime { get; set; }

    public DateTimeOffset? QueueTimeExpiry { get; set; }

    public List<string> SpecificMachineIds { get; set; } = new();

    public List<string> ExcludedMachineIds { get; set; } = new();

    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public List<int> SkipActionIds { get; set; } = new();
}

public class RollbackDeploymentResponse : SquidResponse<CreateDeploymentResponseData>
{
}
