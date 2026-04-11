namespace Squid.Core.Services.DeploymentExecution.Intents;

/// <summary>
/// Intent to build a Kustomize overlay and apply the resulting manifests to a cluster.
/// The renderer is responsible for invoking <c>kustomize build &lt;OverlayPath&gt;</c>
/// (directly or via a <c>kubectl apply -k</c> shortcut) and piping the output into
/// <c>kubectl apply</c>, honouring <see cref="AdditionalArgs"/> on the <c>kustomize</c>
/// invocation and <see cref="ServerSideApply"/>/<see cref="FieldManager"/>/<see cref="ForceConflicts"/>
/// on the apply. Unlike <see cref="KubernetesApplyIntent"/> there are no pre-rendered
/// <c>YamlFiles</c> — the manifests only exist after the overlay is built on the target.
/// </summary>
public sealed record KubernetesKustomizeIntent : ExecutionIntent
{
    /// <summary>
    /// Path to the Kustomize overlay directory, relative to the staged package or
    /// working directory. Defaults to <c>"."</c> when the overlay is at the package root.
    /// </summary>
    public required string OverlayPath { get; init; }

    /// <summary>
    /// Optional path to a non-default <c>kustomize</c> executable. Empty string means
    /// "use <c>kubectl apply -k</c>" (or whichever binary the renderer chooses).
    /// </summary>
    public string CustomKustomizePath { get; init; } = string.Empty;

    /// <summary>
    /// Additional arguments appended to the <c>kustomize build</c> invocation
    /// (e.g. <c>--enable-helm</c>, <c>--load-restrictor LoadRestrictionsNone</c>).
    /// </summary>
    public string AdditionalArgs { get; init; } = string.Empty;

    /// <summary>The target namespace. Empty string means "use the kubeconfig default namespace".</summary>
    public string Namespace { get; init; } = string.Empty;

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
}
