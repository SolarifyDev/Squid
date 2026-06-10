using System.Text.Json.Serialization;

namespace Squid.Message.Enums.Deployments;

/// <summary>
/// What to do when a deployment target is UNAVAILABLE at the start of, or becomes
/// unavailable during, a deployment. The project default for an unconfigured project is
/// <see cref="FailDeployment"/> (fail fast rather than silently skip an unreachable target
/// and report success — see <c>TransientDeploymentTargetsDto</c>). <see cref="SkipAndContinue"/>
/// is the opt-in lenient mode. Note value 0 remains <see cref="SkipAndContinue"/> as the
/// stable wire encoding; the default is applied at the settings DTO, not by enum ordinal.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnavailableDeploymentTargetBehavior
{
    SkipAndContinue = 0,
    FailDeployment = 1,
}

/// <summary>
/// What to do with UNHEALTHY deployment targets at the start of a deployment. Default
/// (value 0) = <see cref="Exclude"/>, which preserves Squid's historical behaviour
/// (unhealthy targets are skipped and removed) — so wiring this is non-breaking.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UnhealthyDeploymentTargetBehavior
{
    Exclude = 0,
    DoNotExclude = 1,
}
