using System.Text.Json.Serialization;

namespace Squid.Message.Enums.Deployments;

/// <summary>
/// What to do when a deployment target is UNAVAILABLE at the start of, or becomes
/// unavailable during, a deployment. Default (value 0) = <see cref="SkipAndContinue"/>,
/// which preserves Squid's historical behaviour (unavailable targets are skipped and
/// removed from the deployment) — so wiring this is non-breaking.
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
