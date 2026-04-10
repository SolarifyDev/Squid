using System.Collections.Generic;
using Squid.Core.Services.DeploymentExecution.Script.Files;

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

    /// <summary>When true, invoke <c>kubectl apply --server-side</c> for conflict-free apply.</summary>
    public bool ServerSideApply { get; init; }
}
