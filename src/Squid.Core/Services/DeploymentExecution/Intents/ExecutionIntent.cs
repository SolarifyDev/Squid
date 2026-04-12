using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Script.Files;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// A semantic, transport-agnostic description of what a deployment action wants to accomplish.
/// Produced by <c>IActionHandler.DescribeIntentAsync</c> in Phase 9. A per-transport
/// <c>IIntentRenderer</c> translates the intent into a concrete <c>ScriptExecutionRequest</c>.
///
/// <para>Intents are immutable records — use <c>with</c> expressions to derive modified copies.</para>
///
/// <para>
/// Base fields (cross-cutting, applicable to all subtypes):
/// </para>
/// <list type="bullet">
///   <item><description><see cref="Name"/> — unique short identifier of the intent (e.g. <c>run-script</c>, <c>k8s-apply</c>). Used for logging and registry keys.</description></item>
///   <item><description><see cref="StepName"/> / <see cref="ActionName"/> — the deployment process step/action this intent was derived from. Used for log framing.</description></item>
///   <item><description><see cref="Assets"/> — side files the renderer MUST materialise on the target before script execution (e.g. rendered YAML, helm values, helper bundles).</description></item>
///   <item><description><see cref="RequiredCapabilities"/> — feature strings from <see cref="IntentCapabilityKeys"/> that the renderer/transport must satisfy; feeds <c>ICapabilityValidator</c>.</description></item>
///   <item><description><see cref="Packages"/> — packages that must be staged on the target before execution.</description></item>
///   <item><description><see cref="Timeout"/> — optional wall-clock timeout for the whole action; <c>null</c> defers to transport/system defaults.</description></item>
/// </list>
/// </summary>
public abstract record ExecutionIntent
{
    /// <summary>Short semantic identifier (e.g. <c>run-script</c>, <c>k8s-apply</c>, <c>helm-upgrade</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Name of the deployment process step that produced this intent.</summary>
    public string StepName { get; init; } = string.Empty;

    /// <summary>Name of the deployment action that produced this intent.</summary>
    public string ActionName { get; init; } = string.Empty;

    /// <summary>Side files the renderer must materialise on the target (rendered YAML, helm values, etc.).</summary>
    public IReadOnlyList<DeploymentFile> Assets { get; init; } = Array.Empty<DeploymentFile>();

    /// <summary>Feature strings from <see cref="IntentCapabilityKeys"/> required by the renderer/transport.</summary>
    public IReadOnlySet<string> RequiredCapabilities { get; init; } = EmptyCapabilities;

    /// <summary>Packages that must be staged on the target before script execution.</summary>
    public IReadOnlyList<IntentPackageReference> Packages { get; init; } = Array.Empty<IntentPackageReference>();

    /// <summary>Optional wall-clock timeout for the entire action; <c>null</c> defers to transport default.</summary>
    public TimeSpan? Timeout { get; init; }

    private static readonly IReadOnlySet<string> EmptyCapabilities
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
