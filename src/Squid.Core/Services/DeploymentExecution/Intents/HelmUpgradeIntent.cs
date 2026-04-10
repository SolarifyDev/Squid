using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Script.Files;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to run <c>helm upgrade --install</c> for a chart release. The renderer chooses
/// the exact command flags and stages the values file (if any) via the transport's file
/// transfer mechanism.
/// </summary>
public sealed record HelmUpgradeIntent : ExecutionIntent
{
    /// <summary>The Helm release name (<c>helm upgrade &lt;release&gt; &lt;chart&gt;</c>).</summary>
    public required string ReleaseName { get; init; }

    /// <summary>
    /// Chart reference — either an OCI path (<c>oci://registry/repo/chart</c>), a
    /// repo-qualified chart (<c>bitnami/nginx</c>), or a local path relative to a
    /// staged package root.
    /// </summary>
    public required string ChartReference { get; init; }

    /// <summary>The target namespace. Empty string means "use the kubeconfig default namespace".</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>Optional rendered <c>values.yaml</c> file; passed via <c>-f</c>.</summary>
    public DeploymentFile? ValuesFile { get; init; }

    /// <summary>
    /// Additional <c>--set key=value</c> overrides applied on top of <see cref="ValuesFile"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> InlineValues { get; init; }
        = new Dictionary<string, string>();
}
