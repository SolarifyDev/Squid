using System.Diagnostics;
using Serilog;

namespace Squid.Tentacle.Core;

/// <summary>
/// P0-Phase10.1 (audit C.3) — probes the agent's K8s RBAC permissions via
/// <c>kubectl auth can-i &lt;verb&gt; &lt;resource&gt;</c> and surfaces the
/// results via <see cref="Squid.Message.Contracts.Tentacle.CapabilitiesResponse.Metadata"/>.
///
/// <para><b>Why this exists</b>: pre-Phase-10.1, an agent whose ServiceAccount
/// RBAC was revoked / namespace was deleted would still report "healthy" to
/// the server (Halibut polling worked fine). The first deploy then failed
/// with a cryptic kubectl Forbidden error. Now the server's
/// <see cref="Squid.Core.Services.DeploymentExecution.Kubernetes.KubernetesAgentHealthCheckStrategy"/>
/// inspects the metadata keys this class populates and fails fast with an
/// actionable message naming the missing resource.</para>
///
/// <para><b>Probe scope</b>: only the resources whose CREATE permission is
/// required for at least one of Squid's deploy paths. Each is named in a
/// constant so the server-side strategy + alerting rules + this class drift
/// in lockstep.</para>
///
/// <para><b>Pod-only probing</b>: <see cref="Inspect"/> is a no-op outside a
/// K8s pod (detected via <c>KUBERNETES_SERVICE_HOST</c>). Non-K8s tentacles
/// don't have RBAC, so probing them would just produce kubectl-not-found
/// noise.</para>
///
/// <para><b>Namespace scope (limitation)</b>: <c>kubectl auth can-i</c> with
/// no <c>--namespace</c> flag checks permissions in the pod's CURRENT
/// namespace (the one the ServiceAccount lives in, bound at pod runtime).
/// If the agent pod is in <c>kube-system</c> but operators deploy targets
/// to <c>default</c> namespace, the probe result is a <i>health-check
/// approximation</i>, not a per-deploy-target authorization decision.
/// Operators should either (a) put the agent in the target namespace, or
/// (b) grant the ServiceAccount equivalent permissions in BOTH namespaces.
/// True per-deploy-namespace verification needs the deploy target's
/// namespace as input — that's a per-request check, not a per-startup
/// probe, and is tracked as a future ARCH item.</para>
/// </summary>
public static class KubernetesRbacInspector
{
    /// <summary>Metadata key for "can the agent create pods?" — pinned literal.</summary>
    public const string MetaCanCreatePods = "kubernetes.canCreatePods";

    /// <summary>Metadata key for "can the agent create configmaps?" — pinned literal.</summary>
    public const string MetaCanCreateConfigMaps = "kubernetes.canCreateConfigMaps";

    /// <summary>Metadata key for "can the agent create secrets?" — pinned literal.</summary>
    public const string MetaCanCreateSecrets = "kubernetes.canCreateSecrets";

    /// <summary>
    /// kubectl-resource-name → metadata-key mapping. Adding a new
    /// deploy-critical permission means: add a constant + add the entry
    /// here + add the entry on the server-side strategy's
    /// <c>DeployCriticalPermissions</c> array — three places to update,
    /// caught by tests pinning the literals on both sides.
    /// </summary>
    private static readonly (string Resource, string MetaKey)[] ProbeTargets =
    {
        ("pods", MetaCanCreatePods),
        ("configmaps", MetaCanCreateConfigMaps),
        ("secrets", MetaCanCreateSecrets),
    };

    private const int ProbeTimeoutMs = 5_000;

    /// <summary>
    /// Returns metadata key/value pairs for the agent's RBAC permissions.
    /// Empty when not running in a K8s pod. Each value is "yes" or "no".
    /// </summary>
    public static Dictionary<string, string> Inspect()
    {
        var result = new Dictionary<string, string>();

        if (!IsRunningInKubernetesPod()) return result;

        foreach (var (resource, metaKey) in ProbeTargets)
        {
            try
            {
                var exitCode = ProbeAuthCanI("create", resource);
                result[metaKey] = ParseAuthCanIExitCode(exitCode);
            }
            catch (Exception ex)
            {
                // Swallow per-probe failures (kubectl missing, timeout, etc.) —
                // map to "no" so the server-side health check fails closed
                // rather than silently passing.
                Log.Warning(ex, "[K8S-RBAC] Probe failed for create {Resource} — recording 'no'", resource);
                result[metaKey] = "no";
            }
        }

        Log.Information(
            "[K8S-RBAC] Probed {Count} permission(s); results: {Results}",
            result.Count, string.Join(", ", result.Select(kv => $"{kv.Key}={kv.Value}")));

        return result;
    }

    /// <summary>
    /// Detects whether the process is running inside a K8s pod. The kubelet
    /// automounts <c>KUBERNETES_SERVICE_HOST</c> into every pod's environment
    /// — its presence is the standard "I'm in a pod" signal.
    /// </summary>
    public static bool IsRunningInKubernetesPod()
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        return !string.IsNullOrWhiteSpace(host);
    }

    /// <summary>
    /// Maps a <c>kubectl auth can-i</c> exit code to the metadata-value
    /// string. Exit 0 = permission granted ("yes"). Anything else = denied
    /// or RBAC system error — both fail-closed to "no" so the server-side
    /// health check raises an actionable signal rather than silently passing.
    ///
    /// <c>internal</c> for direct unit testing.
    /// </summary>
    internal static string ParseAuthCanIExitCode(int exitCode) => exitCode == 0 ? "yes" : "no";

    /// <summary>
    /// Shells out to <c>kubectl auth can-i &lt;verb&gt; &lt;resource&gt;</c>.
    /// Returns the process exit code. Throws on kubectl-not-found or timeout.
    /// </summary>
    private static int ProbeAuthCanI(string verb, string resource)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "kubectl",
            Arguments = $"auth can-i {verb} {resource}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("kubectl process failed to start");

        if (!proc.WaitForExit(ProbeTimeoutMs))
        {
            try { proc.Kill(true); } catch { /* best-effort */ }
            throw new TimeoutException($"kubectl auth can-i {verb} {resource} exceeded {ProbeTimeoutMs}ms");
        }

        return proc.ExitCode;
    }
}
