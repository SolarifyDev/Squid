using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to apply a set of Kubernetes YAML manifests to a cluster via <c>kubectl apply</c>
/// (or equivalent). Covers raw YAML, container deploys, config maps, secrets, services,
/// ingresses, and kustomize — the renderer decides which kubectl subcommand to use based
/// on file naming and annotations.
/// </summary>
public sealed record KubernetesApplyIntent : ExecutionIntent
{
    /// <summary>
    /// The rendered YAML manifests to apply. Each file's <c>RelativePath</c> is typically
    /// under <c>content/</c> (e.g. <c>content/deployment.yaml</c>, <c>content/service.yaml</c>).
    /// </summary>
    public required IReadOnlyList<DeploymentFile> YamlFiles { get; init; }

    /// <summary>The target namespace. Empty string means "use the kubeconfig default namespace".</summary>
    public string Namespace { get; init; } = string.Empty;

    /// <summary>
    /// The script syntax the renderer should target when emitting the kubectl apply
    /// pipeline (Bash vs PowerShell). Defaults to <see cref="ScriptSyntax.Bash"/>.
    /// </summary>
    public ScriptSyntax Syntax { get; init; } = ScriptSyntax.Bash;

    /// <summary>When true, invoke <c>kubectl apply --server-side</c> for conflict-free apply.</summary>
    public bool ServerSideApply { get; init; }

    /// <summary>
    /// Field manager name for server-side apply. Ignored when <see cref="ServerSideApply"/>
    /// is false. Defaults to <c>"squid-deploy"</c>.
    /// </summary>
    public string FieldManager { get; init; } = "squid-deploy";

    /// <summary>
    /// When true, pass <c>--force-conflicts</c> alongside server-side apply. Ignored when
    /// <see cref="ServerSideApply"/> is false.
    /// </summary>
    public bool ForceConflicts { get; init; }

    /// <summary>
    /// When true, the renderer appends a rollout / wait-for-condition status check
    /// for every Deployment / StatefulSet / DaemonSet / Job found in <see cref="YamlFiles"/>
    /// after the apply commands.
    /// </summary>
    public bool ObjectStatusCheck { get; init; }

    /// <summary>
    /// Timeout in seconds for each status check probe. Ignored when
    /// <see cref="ObjectStatusCheck"/> is false. Defaults to <c>300</c>.
    /// </summary>
    public int StatusCheckTimeoutSeconds { get; init; } = 300;
}
