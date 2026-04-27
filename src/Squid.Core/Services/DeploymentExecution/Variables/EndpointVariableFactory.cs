using System.Text.Json;
using Squid.Message.Json;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Generic helper for endpoint JSON deserialization and <see cref="VariableDto"/>
/// construction. Used by EVERY transport's <c>IEndpointVariableContributor</c>
/// and <c>IHealthCheckStrategy</c> — KubernetesApi, KubernetesAgent, Ssh,
/// Tentacle, OpenClaw — to materialise the endpoint payload and emit
/// per-target context variables.
///
/// <para><b>Namespace history</b>: pre-Phase-8 this lived under
/// <c>Squid.Core.Services.DeploymentExecution.Kubernetes</c> for historical
/// reasons (it was extracted during the K8s refactor). That made every
/// non-K8s transport import a K8s-flavoured namespace just to deserialize
/// its OWN endpoint JSON — a genericity smell visible in OpenClaw and Ssh
/// health-check files. The class has zero K8s-specific logic, so this
/// move is namespace-only — file relocated to the <c>Variables</c> folder
/// next to the other variable helpers (<c>EffectiveVariableBuilder</c>,
/// <c>OutputVariableMerger</c>, <c>AccountVariableExpander</c>).</para>
/// </summary>
public static class EndpointVariableFactory
{
    public static VariableDto Make(string name, string value, bool isSensitive = false) => new()
    {
        Name = name,
        Value = value,
        Description = string.Empty,
        Type = Message.Enums.VariableType.String,
        IsSensitive = isSensitive,
        LastModifiedDate = DateTimeOffset.UtcNow,
        LastModifiedBy = 0
    };

    public static T TryDeserialize<T>(string json) where T : class
    {
        if (string.IsNullOrEmpty(json)) return null;

        try { return JsonSerializer.Deserialize<T>(json, SquidJsonDefaults.CaseInsensitive); }
        catch { return null; }
    }
}
