namespace Squid.Tentacle.Tests.Support.Collections;

/// <summary>
/// P1-Phase12.B-audit-fix — serialises tests that mutate process-global env
/// vars consumed by Tentacle production code (e.g.
/// <c>SQUID_TENTACLE_USE_WINDOWS_POWERSHELL</c>).
///
/// <para><b>Why this exists</b>: xUnit runs methods within a class
/// sequentially but classes in parallel. ProcessLauncherFactory reads its
/// env var on every <c>Resolve()</c> call. If two unrelated test classes
/// both <c>SetEnvironmentVariable</c> at the same instant, one's <c>finally</c>
/// can overwrite the other's expected state mid-run, leaking the wrong value
/// into a third test's <c>Resolve()</c>. Symptom: nightly Windows runner flake
/// where <c>Pwsh_OptInWindowsPowerShellExe_ViaEnvVar_RunsSuccessfully</c>
/// races with <c>Factory_PowerShell_OnWindows_DefaultRoutesToPwshCore_NoBreakingChange</c>.
/// </para>
///
/// <para><b>Same pattern as</b>: Phase-10.3-audit fix in
/// <c>KubernetesRbacInspectorTests</c> (<c>[Collection("KubernetesEnvVarMutators")]</c>)
/// for the same class of env-var-race hazard against KUBERNETES_SERVICE_HOST.</para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TentacleEnvVarMutatorsCollection
{
    public const string Name = "Tentacle.EnvVarMutators";
}
