using System;
using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to run <c>helm upgrade --install</c> for a chart release. The renderer chooses
/// the exact command flags and stages any values files via the transport's file transfer
/// mechanism. Semantic fields (wait/timeout/reset-values/etc.) survive into the intent
/// layer so a single renderer can reproduce the legacy handler's behaviour without
/// re-reading action properties.
/// </summary>
public sealed record HelmUpgradeIntent : ExecutionIntent
{
    /// <summary>The Helm release name (<c>helm upgrade &lt;release&gt; &lt;chart&gt;</c>).</summary>
    public required string ReleaseName { get; init; }

    /// <summary>
    /// Chart reference — either an OCI path (<c>oci://registry/repo/chart</c>), a
    /// repo-qualified chart (<c>squid-helm-repo/nginx</c>), or a local path relative to a
    /// staged package root. When a <see cref="Repository"/> is attached, this is a
    /// repo-qualified reference.
    /// </summary>
    public required string ChartReference { get; init; }

    /// <summary>The script syntax to use for the rendered shell script (Bash or PowerShell).</summary>
    public ScriptSyntax Syntax { get; init; } = ScriptSyntax.Bash;

    /// <summary>The target namespace. Empty string means "use the kubeconfig default namespace".</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// Optional chart version (passed via <c>--version</c>). Empty means "use the latest
    /// available version". Typically only set when <see cref="Repository"/> is populated
    /// from a feed-backed deployment package.
    /// </summary>
    public string ChartVersion { get; init; } = string.Empty;

    /// <summary>
    /// Rendered values files to stage alongside the chart. Empty means no <c>-f</c> flags.
    /// May contain multiple entries when the action's <c>ValueSources</c> supplies more
    /// than one inline-YAML block.
    /// </summary>
    public IReadOnlyList<DeploymentFile> ValuesFiles { get; init; } = Array.Empty<DeploymentFile>();

    /// <summary>
    /// Additional <c>--set key=value</c> overrides applied on top of <see cref="ValuesFiles"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string> InlineValues { get; init; }
        = new Dictionary<string, string>();

    /// <summary>
    /// Optional path to a non-default <c>helm</c> executable. Empty string means "use
    /// the <c>helm</c> binary on <c>PATH</c>".
    /// </summary>
    public string CustomHelmExecutable { get; init; } = string.Empty;

    /// <summary>When true, pass <c>--reset-values</c> to <c>helm upgrade</c>. Defaults to true to match legacy behaviour.</summary>
    public bool ResetValues { get; init; } = true;

    /// <summary>When true, pass <c>--wait</c> to block until resources are ready.</summary>
    public bool Wait { get; init; }

    /// <summary>When true, pass <c>--wait-for-jobs</c> so <c>--wait</c> also waits on completed jobs.</summary>
    public bool WaitForJobs { get; init; }

    /// <summary>
    /// Optional timeout value passed via <c>--timeout</c> (e.g. <c>"5m"</c>, <c>"600s"</c>).
    /// Empty means "use the helm default".
    /// </summary>
    public string Timeout { get; init; } = string.Empty;

    /// <summary>
    /// Additional raw arguments appended to the <c>helm upgrade</c> invocation
    /// (e.g. <c>"--debug --dry-run"</c>). Empty when no extra flags are requested.
    /// </summary>
    public string AdditionalArgs { get; init; } = string.Empty;

    /// <summary>
    /// Optional repository to add before running the upgrade. Populated when the action
    /// references a chart feed; <c>null</c> for local chart paths or OCI references the
    /// renderer can pull directly.
    /// </summary>
    public HelmRepository? Repository { get; init; }
}

/// <summary>
/// A Helm repository that must be added (and credentials applied) before running
/// <c>helm upgrade</c>. The renderer is responsible for issuing <c>helm repo add</c> /
/// <c>helm repo update</c> in the appropriate syntax for the target transport.
/// </summary>
public sealed record HelmRepository
{
    /// <summary>The local alias used to register the repo (<c>helm repo add &lt;Name&gt; &lt;Url&gt;</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The repository URL.</summary>
    public required string Url { get; init; }

    /// <summary>Optional basic-auth username for private repositories.</summary>
    public string? Username { get; init; }

    /// <summary>Optional basic-auth password for private repositories. Treat as sensitive.</summary>
    public string? Password { get; init; }
}
