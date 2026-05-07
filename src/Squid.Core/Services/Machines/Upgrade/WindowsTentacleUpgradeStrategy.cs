using System.Reflection;
using System.Text;
using Halibut;
using Squid.Core.Halibut.Resilience;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Machines.Upgrade.Methods;
using Squid.Message.Commands.Machine;
using Squid.Message.Contracts.Tentacle;
using Squid.Message.Enums;

namespace Squid.Core.Services.Machines.Upgrade;

/// <summary>
/// registered counterpart to <see cref="LinuxTentacleUpgradeStrategy"/>
/// that performs the in-place upgrade of a Windows Tentacle. Same Halibut RPC
/// path the deployment pipeline uses for "Run a Script" steps; same outcome
/// contract (exception → status mapping, <see cref="MachineUpgradeOutcome.AgentVersionMayHaveChanged"/>
/// boolean for cache invalidation, 5-minute timeout strictly less than the
/// orchestrator's lock TTL); same operator-readable embedded template
/// architecture.
///
/// <para><b>Detach mechanism — divergence from Linux:</b> Linux uses
/// <c>exec sudo systemd-run --scope</c> at the end of Phase A to re-exec
/// the bash script into a transient cgroup that survives <c>systemctl restart
/// squid-tentacle</c>. Windows has no in-process cgroup equivalent — once
/// the squid-tentacle service is stopped, every child process under it dies.
/// The Windows analog is <b>Task Scheduler one-shot task as SYSTEM</b>: a
/// short outer wrapper script (delivered via the same Halibut RPC) writes
/// the rendered inner template to <c>%ProgramData%\Squid\Tentacle\upgrade\dispatch-&lt;TaskName&gt;.ps1</c>
/// (per-task to avoid concurrent-dispatch races on a shared file),
/// registers a one-shot task pointing at that file with <c>/RU SYSTEM /Z /F</c>,
/// triggers it via <c>/Run</c>, then exits 0. The Task-Scheduler-launched
/// process tree is independent of the squid-tentacle service, so Phase B's
/// <c>Stop-Service</c> + <c>Move-Item</c> + <c>Start-Service</c> sequence
/// runs to completion even though the original Halibut connection is long
/// gone. The <c>/Z</c> flag auto-deletes the task after run so the Task
/// Scheduler library doesn't accumulate stale entries.</para>
///
/// <para><b>Status mapping — divergence from Linux:</b> Linux observes a
/// mid-script Halibut disconnect (the bash <c>exec</c> kills the original
/// PID) and maps that to <see cref="MachineUpgradeStatus.Initiated"/>. Windows
/// observes a clean <c>exit 0</c> from the outer wrapper and maps THAT to
/// <see cref="MachineUpgradeStatus.Initiated"/> — the wrapper finishing
/// successfully means the detached task was scheduled, not that the upgrade
/// is complete. The actual outcome comes via the rapid-polling burst
/// (already wired by <c>MachineUpgradeService.ScheduleRapidPolling</c> for
/// every dispatch) reading <c>last-upgrade.json</c> via the next Capabilities
/// probe. Both Linux and Windows therefore arrive at <c>Initiated</c> after
/// a successful kick-off, just via different signals.</para>
///
/// <para><b>Idempotency:</b> the .ps1 template owns a per-host lock
/// (<c>%ProgramData%\Squid\Tentacle\upgrade\upgrade.lock</c>); a redelivered
/// or concurrently-dispatched upgrade is a no-op. The orchestrator's Redis
/// lock at <c>MachineUpgradeService.LockExpiry</c> already prevents server-side
/// dual-dispatch.</para>
/// </summary>
public sealed class WindowsTentacleUpgradeStrategy : IMachineUpgradeStrategy
{
    /// <summary>
    /// Embedded PowerShell template loaded once per process via <see cref="Lazy{T}"/>.
    /// The strategy reads-replaces-base64-encodes-wraps it on every dispatch;
    /// the file IO happens at most once per process even under concurrent load.
    /// </summary>
    private const string EmbeddedScriptResource = "Squid.Core.Resources.Upgrade.upgrade-windows-tentacle.ps1";

    /// <summary>
    /// Default install dir matches 's <c>install-tentacle.ps1</c>
    /// — operators upgrading a deployment that used the canonical installer
    /// don't need to override. Pinned per Rule 8 by
    /// <c>WindowsTentacleUpgradeStrategyTests.DefaultInstallDir_PinnedToCanonicalProgramFilesPath</c>.
    /// </summary>
    internal const string DefaultInstallDir = @"C:\Program Files\Squid Tentacle";

    /// <summary>
    /// Default service name matches what <c>WindowsServiceHost.Install</c>
    /// creates and what <c>install-tentacle.ps1</c> registers.
    /// Pinned per Rule 8.
    /// </summary>
    internal const string DefaultServiceName = "squid-tentacle";

    /// <summary>
    /// Operator override for the zip download base. Air-gapped / fork
    /// deployments point at a private mirror; defaults to GitHub Releases.
    /// Naming convention <c>{base}/{version}/squid-tentacle-{version}-{rid}.zip</c>
    /// stays canonical so a private mirror can copy the GitHub release tree.
    /// </summary>
    public const string DownloadBaseUrlEnvVar = "SQUID_TARGET_WINDOWS_TENTACLE_DOWNLOAD_BASE_URL";

    /// <summary>
    /// Operator override for the post-restart healthcheck URL the .ps1
    /// template polls to confirm the new binary came up. Same shape as
    /// <see cref="LinuxTentacleUpgradeStrategy.HealthcheckUrlEnvVar"/>.
    /// </summary>
    public const string HealthcheckUrlEnvVar = "SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_URL";

    /// <summary>
    /// Operator override for the post-restart healthcheck poll count.
    /// Default <see cref="DefaultHealthcheckRetries"/> (30) × 2s sleep =
    /// 60s wait window; tests override to 1 to bypass the death-wait
    /// (<see cref="LocalReleaseMirror"/> healthcheck endpoint is intentionally
    /// unreachable in the lifecycle E2E since the test service doesn't
    /// expose HTTP — every Phase B run currently sits in the 60s loop
    /// before the .ps1's "::warning::" + proceed path fires).
    ///
    /// <para><b>Air-gap operator value</b>: deployments with slow-starting
    /// services (e.g. heavy plugin enumeration on first run, &gt;60s) get
    /// false-warnings every upgrade today; setting this to 90 (3 min) makes
    /// the post-restart wait realistic for those environments without
    /// changing default behaviour.</para>
    ///
    /// <para>Pinned per Rule 8 by
    /// <c>WindowsTentacleUpgradeStrategyTests.HealthcheckRetriesEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string HealthcheckRetriesEnvVar = "SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_RETRIES";

    private const string DefaultDownloadBaseUrl = "https://github.com/SolarifyDev/Squid/releases/download";
    private const string DefaultHealthcheckUrl = "http://127.0.0.1:8080/healthz";

    /// <summary>
    /// Default healthcheck poll count. 30 × 2s sleep per attempt = 60s
    /// total wait window after Start-Service. Generous enough for stock
    /// agent boot (~3-5s) plus runtime / plugin warmup (~10-30s) on
    /// reasonable hardware. Operators with slower starts override via
    /// <see cref="HealthcheckRetriesEnvVar"/>.
    /// </summary>
    internal const int DefaultHealthcheckRetries = 30;

    /// <summary>
    /// Operator override for the post-restart healthcheck failure mode.
    /// Default behaviour (`false`) is "warning + proceed" — matching
    /// Octopus Tentacle's permissive policy where the capabilities probe
    /// detects a non-responsive new binary on the next health probe and
    /// reports it server-side. Setting this env var to `true` enables
    /// strict mode: a healthcheck timeout triggers <c>Invoke-Rollback</c>
    /// + restores the previous binary.
    ///
    /// <para><b>When to enable</b>: production fleets where the agent's
    /// <c>/healthz</c> endpoint is the canonical liveness contract — if
    /// it doesn't respond, the agent is functionally dead and rolling
    /// back to the previous-known-working version is preferred over
    /// leaving the operator with a Stopped service. Air-gap fleets where
    /// the upgrade is the only restart-eligible window.</para>
    ///
    /// <para><b>When to leave default</b>: deployments where new agents
    /// take a variable time to come up healthy (heavy plugin enumeration,
    /// startup migrations) and a temporary timeout shouldn't trigger a
    /// rollback that disrupts a successful upgrade.</para>
    ///
    /// <para>Pinned per Rule 8 by
    /// <c>WindowsTentacleUpgradeStrategyTests.HealthcheckFatalEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string HealthcheckFatalEnvVar = "SQUID_TARGET_WINDOWS_TENTACLE_HEALTHCHECK_FATAL";

    /// <summary>
    /// Operator override for the wall-clock timeout the .ps1 waits on
    /// `$svc.WaitForStatus('Running', ...)` and `WaitForStatus('Stopped', ...)`
    /// after Start-Service / Stop-Service. Default <see cref="DefaultServiceTimeoutSeconds"/>
    /// (30s) covers stock-agent boot (~3-5s) plus runtime warmup (~10-30s).
    ///
    /// <para><b>Why this matters</b>: a heavyweight agent (heavy plugin
    /// enumeration, .NET tiered JIT cold start, AV scanning a 50MB binary
    /// before the first run) can take >30s to reach the SCM RUNNING state.
    /// Without this override, the post-Start `WaitForStatus` times out →
    /// Invoke-Rollback fires → operator sees ROLLED_BACK on what was
    /// actually a successful (just-slow) upgrade.</para>
    ///
    /// <para><b>Single var for both Stop and Start</b>: Stop-Service's
    /// 30s default is rarely the bottleneck (a service stuck Stopping is
    /// usually a CanStop=false / OnStop deadlock bug, not slowness).
    /// Operators tuning for slow-start environments rarely need different
    /// numbers for Stop vs Start. Keep one knob; revisit if 5+ env vars
    /// triggers refactor (Rule 7).</para>
    ///
    /// <para>Pinned per Rule 8 by
    /// <c>WindowsTentacleUpgradeStrategyTests.ServiceTimeoutSecondsEnvVar_ConstantNamePinned</c>.</para>
    /// </summary>
    public const string ServiceTimeoutSecondsEnvVar = "SQUID_TARGET_WINDOWS_TENTACLE_SERVICE_TIMEOUT_SECONDS";

    /// <summary>
    /// Default wall-clock cap on each <c>WaitForStatus</c> call (Stop and
    /// Start). 30s × ~5s typical RUNNING transition = 6x margin. Operators
    /// with heavyweight agents override via <see cref="ServiceTimeoutSecondsEnvVar"/>.
    /// </summary>
    internal const int DefaultServiceTimeoutSeconds = 30;

    /// <summary>
    /// Wall-clock cap for a single upgrade dispatch. Mirrors Linux's
    /// 5-minute cap — must stay strictly less than
    /// <c>MachineUpgradeService.LockExpiry</c> (7 min) so an abandoned
    /// dispatch can't hold the Redis lock past TTL.
    /// </summary>
    internal static readonly TimeSpan UpgradeScriptTimeout = TimeSpan.FromMinutes(5);

    private static readonly Lazy<string> _scriptTemplate = new(LoadEmbeddedScript);

    /// <summary>
    /// Default method order — zip-only in .  will
    /// prepend chocolatey + MSI. Stateless static instance shared across
    /// all dispatches.
    /// </summary>
    internal static readonly IReadOnlyList<IWindowsUpgradeMethod> DefaultMethodOrder = new IWindowsUpgradeMethod[]
    {
        new ZipUpgradeMethod()
    };

    private readonly IHalibutClientFactory _halibutClientFactory;
    private readonly IHalibutScriptObserver _observer;

    public WindowsTentacleUpgradeStrategy(IHalibutClientFactory halibutClientFactory, IHalibutScriptObserver observer)
    {
        _halibutClientFactory = halibutClientFactory;
        _observer = observer;
    }

    public bool CanHandle(string communicationStyle, MachineRuntimeCapabilities capabilities)
    {
        var matchesStyle = communicationStyle == nameof(CommunicationStyle.TentaclePolling)
                        || communicationStyle == nameof(CommunicationStyle.TentacleListening);

        if (!matchesStyle) return false;

        // read via the centralized IsWindows property
        // instead of an inline string comparison. Same null-defensive
        // semantic as before. The literal "Windows" string lives once in
        // AgentOperatingSystems and the agent reports it from the same
        // const — a rename surfaces as a build-time symbol-not-found,
        // not a runtime "no strategy registered" silent breakage.
        if (capabilities == null) return false;

        return capabilities.IsWindows;
    }

    public async Task<MachineUpgradeOutcome> UpgradeAsync(Machine machine, string targetVersion, CancellationToken ct)
    {
        var validation = ValidateRequest(machine, targetVersion);

        if (validation != null) return validation;

        var endpoint = HalibutMachineExecutionStrategy.ParseMachineEndpoint(machine);

        if (endpoint == null) return Failed($"Machine '{machine.Name}' has no usable Halibut endpoint — cannot dispatch upgrade");

        var command = BuildStartScriptCommand(machine, targetVersion);
        var scriptClient = _halibutClientFactory.CreateClient(endpoint);

        Log.Information("[Upgrade] Dispatching Windows upgrade to {Machine} → version {Version}", machine.Name, targetVersion);

        return await DispatchAsync(machine, command, scriptClient, endpoint, targetVersion, ct).ConfigureAwait(false);
    }

    // ── Validation ───────────────────────────────────────────────────────────

    private static MachineUpgradeOutcome ValidateRequest(Machine machine, string targetVersion)
    {
        if (machine == null) return Failed("machine is required");

        if (string.IsNullOrWhiteSpace(targetVersion)) return Failed("targetVersion is required for WindowsTentacle upgrade");

        return null;
    }

    // ── Halibut dispatch + observation ───────────────────────────────────────

    private async Task<MachineUpgradeOutcome> DispatchAsync(Machine machine, StartScriptCommand command, IAsyncScriptService scriptClient, ServiceEndPoint endpoint, string targetVersion, CancellationToken ct)
    {
        // Same dispatchAcked discipline as the Linux side. A HalibutClientException
        // BEFORE this flag flips means the wrapper script was never queued
        // → Failed. AFTER it flipped means agent disconnect during the
        // (very fast) wrapper run; treat as Initiated since the detached
        // schtasks task most-likely already scheduled.
        var dispatchAcked = false;

        try
        {
            var startResponse = await scriptClient.StartScriptAsync(command).ConfigureAwait(false);
            dispatchAcked = true;

            var result = await _observer.ObserveAndCompleteAsync(machine, scriptClient, command.ScriptTicket, UpgradeScriptTimeout, ct, masker: null, initialStartResponse: startResponse, endpoint: endpoint).ConfigureAwait(false);

            return InterpretScriptResult(result, targetVersion);
        }
        catch (HalibutClientException ex) when (dispatchAcked)
        {
            return InterpretMidScriptDisconnect(machine, ex);
        }
        catch (HalibutClientException ex)
        {
            return InterpretPreDispatchFailure(machine, ex);
        }
        catch (AgentUnreachableException ex) when (dispatchAcked)
        {
            return InterpretLivenessProbeAbort(machine, ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Upgrade] Unexpected error upgrading {Machine}", machine.Name);

            return Failed($"Unexpected error: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static MachineUpgradeOutcome InterpretScriptResult(Squid.Core.Services.DeploymentExecution.Script.ScriptExecutionResult result, string targetVersion)
    {
        // Windows divergence from Linux (audit / pre-12.E.4 architectural review):
        // the Halibut-connected outer wrapper runs schtasks → exits 0 inside the
        // squid-tentacle service process. Phase A+B run in a detached Task Scheduler
        // process tree that the wrapper has no observation channel to. So
        // result.Success=true ExitCode=0 means "wrapper successfully scheduled the
        // detached task", NOT "upgrade complete". Map to Initiated; the rapid-polling
        // burst (90s × 3s, scheduled by MachineUpgradeService.ScheduleRapidPolling
        // BEFORE strategy dispatch) catches the actual outcome via Capabilities RPC
        // reading last-upgrade.json. AgentVersionMayHaveChanged=true so the cache
        // refreshes when the new version reports back.
        if (result.Success && result.ExitCode == 0)
            return new MachineUpgradeOutcome
            {
                Status = MachineUpgradeStatus.Initiated,
                Detail = $"Upgrade to {targetVersion} dispatched via Task Scheduler one-shot. Outcome confirmed on next health check via reported version.",
                AgentVersionMayHaveChanged = true
            };

        var lastLog = result.LogLines is { Count: > 0 } ll ? ll[^1] : "(no log lines)";

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Failed,
            Detail = $"Upgrade wrapper failed (exit {result.ExitCode}). Last log: {lastLog}",
            // Outer wrapper failure means schtasks /Create or /Run failed
            // before the detached task started → no binary swap occurred →
            // cache stays valid.
            AgentVersionMayHaveChanged = false
        };
    }

    private static MachineUpgradeOutcome InterpretMidScriptDisconnect(Machine machine, HalibutClientException ex)
    {
        // Outer wrapper is fast (<5s typically). Mid-wrapper disconnect is rare
        // but possible if the agent restarts mid-schtasks-call (e.g. operator
        // killing the squid-tentacle.exe process). Treat as Initiated: the
        // schtasks task may have completed registration; outcome confirmed via
        // last-upgrade.json on next health check.
        Log.Information("[Upgrade] Halibut disconnect mid-wrapper for {Machine} (rare — outer wrapper is fast): {Reason}", machine.Name, ex.Message);

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Initiated,
            Detail = "Wrapper dispatched; agent disconnected mid-wrapper. Detached Task Scheduler task may have already registered. Outcome confirmed on next health check.",
            AgentVersionMayHaveChanged = true
        };
    }

    private static MachineUpgradeOutcome InterpretPreDispatchFailure(Machine machine, HalibutClientException ex)
    {
        // Pre-dispatch Halibut failure = wrapper never queued. No detached task,
        // no upgrade. Mirror Linux's clear pre-dispatch error so operators
        // diagnose at the right layer (network/cert/agent-down).
        Log.Warning("[Upgrade] Halibut dispatch failed for {Machine} BEFORE wrapper was queued: {Reason}", machine.Name, ex.Message);

        return Failed(
            $"Upgrade dispatch failed before the agent acknowledged the wrapper script — the upgrade did NOT run. " +
            $"Likely causes: agent process down, polling subscription not registered, server-agent thumbprint trust missing, " +
            $"or network/firewall blocking the polling channel. Halibut detail: {ex.Message}");
    }

    private static MachineUpgradeOutcome InterpretLivenessProbeAbort(Machine machine, AgentUnreachableException ex)
    {
        // Same dual-signal handling as Linux: liveness probe aborts during
        // the wrapper window get the same Initiated treatment as a Halibut
        // mid-script disconnect.
        Log.Information("[Upgrade] Liveness probe tripped mid-wrapper for {Machine} after {Failures} consecutive probe failures",
            machine.Name, ex.ConsecutiveFailures);

        return new MachineUpgradeOutcome
        {
            Status = MachineUpgradeStatus.Initiated,
            Detail = $"Wrapper dispatched; agent went unreachable after {ex.ConsecutiveFailures} consecutive liveness probes. Outcome confirmed on next health check.",
            AgentVersionMayHaveChanged = true
        };
    }

    // ── Script + command construction ────────────────────────────────────────

    private static StartScriptCommand BuildStartScriptCommand(Machine machine, string targetVersion)
    {
        // Same ticket-id discipline as Linux: full GUID + machine id + prefix.
        var ticketId = $"upgrade-{machine.Id}-{Guid.NewGuid():N}";
        var scriptBody = BuildScript(targetVersion);

        return new StartScriptCommand(new ScriptTicket(ticketId), scriptBody, ScriptIsolationLevel.FullIsolation, UpgradeScriptTimeout, null, Array.Empty<string>(), ticketId, TimeSpan.Zero)
        {
            ScriptSyntax = ScriptType.PowerShell
        };
    }

    /// <summary>Test-only seam for inspecting the command without running Halibut dispatch.</summary>
    internal static StartScriptCommand PreviewStartScriptCommand(Machine machine, string targetVersion)
        => BuildStartScriptCommand(machine, targetVersion);

    internal static string BuildScript(string targetVersion)
        => BuildScript(targetVersion, DefaultMethodOrder);

    /// <summary>Test-only overload — lets us assert against custom method orders.</summary>
    internal static string BuildScript(string targetVersion, IReadOnlyList<IWindowsUpgradeMethod> methods)
    {
        var innerScript = RenderInnerScript(targetVersion, methods);
        var innerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(innerScript));

        return BuildOuterWrapper(innerBase64);
    }

    /// <summary>
    /// Renders the inner upgrade-windows-tentacle.ps1 with all server-side
    /// placeholders filled. Exposed internally so tests can assert against
    /// the rendered inner without round-tripping through base64. The outer
    /// wrapper base64-encodes this and embeds it in the .ps1 it dispatches.
    /// </summary>
    internal static string RenderInnerScript(string targetVersion, IReadOnlyList<IWindowsUpgradeMethod> methods)
    {
        // {RID} stays inside the URL and is rewritten to PowerShell's
        // $RID variable on the agent — same pattern as Linux.
        var downloadUrlTemplate = BuildDownloadUrl(targetVersion, "{RID}").Replace("{RID}", "$RID", StringComparison.Ordinal);

        // Render each method's PowerShell snippet in priority order. Each
        // snippet self-contains its $INSTALL_OK gate so order = priority.
        var installMethodsBlock = string.Join("\n\n", methods.Select(m => m.RenderDetectAndInstall(targetVersion)));

        return _scriptTemplate.Value
            .Replace("{{TARGET_VERSION}}", targetVersion, StringComparison.Ordinal)
            .Replace("{{DOWNLOAD_URL}}", downloadUrlTemplate, StringComparison.Ordinal)
            .Replace("{{EXPECTED_SHA256}}", string.Empty, StringComparison.Ordinal)
            .Replace("{{INSTALL_DIR}}", DefaultInstallDir, StringComparison.Ordinal)
            .Replace("{{SERVICE_NAME}}", DefaultServiceName, StringComparison.Ordinal)
            .Replace("{{HEALTHCHECK_URL}}", ResolveHealthcheckUrl(), StringComparison.Ordinal)
            .Replace("{{HEALTHCHECK_RETRIES}}", ResolveHealthcheckRetries().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{HEALTHCHECK_FATAL}}", ResolveHealthcheckFatal() ? "$true" : "$false", StringComparison.Ordinal)
            .Replace("{{SERVICE_TIMEOUT_SECONDS}}", ResolveServiceTimeoutSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{INSTALL_METHODS}}", installMethodsBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// Constructs the Halibut-connected outer wrapper. Decodes the base64
    /// inner content, writes it to <c>%ProgramData%\Squid\Tentacle\upgrade\dispatch-&lt;TaskName&gt;.ps1</c>
    /// (per-task to avoid the concurrent-dispatch race two wrappers would hit
    /// on a shared <c>dispatch.ps1</c>), schedules a one-shot Task Scheduler
    /// task as SYSTEM (cross-dispatch uniqueness via GUID-suffixed task name),
    /// triggers it, then exits 0. Inner script runs in a separate Task
    /// Scheduler process tree — survives Phase B's <c>Stop-Service</c>.
    ///
    /// <para><b>Why base64 not a here-string</b>: PowerShell here-strings
    /// (<c>@'...'@</c>) terminate at <c>'@</c> at start of line. A future
    /// install method snippet (<c>MsiUpgradeMethod</c>, etc.) might happen
    /// to include such a sequence in a heredoc-style log message. Base64
    /// encoding makes the inner content opaque to the wrapper's lexer.
    /// Operators can still inspect the inner via the per-task dispatch
    /// file under <c>%ProgramData%\Squid\Tentacle\upgrade\</c> until it
    /// auto-cleans (1h cleanup pass on next dispatch). Audit pre-12.E.4.</para>
    /// </summary>
    internal static string BuildOuterWrapper(string innerBase64)
    {
        // C# 11 raw-string interpolation with $$ — wrap interpolations in {{ }}
        // (two each side) so PowerShell's literal `{` and `}` braces don't
        // need escaping. Result is operator-readable plain PowerShell.
        //
        // History (why we DON'T use schtasks.exe directly):
        //   The earlier wrapper used `schtasks.exe /Create /SC ONCE /Z` which
        //   on Windows Server 2022 fails with `(41,4):EndBoundary:` + "task
        //   XML missing required element or attribute". The /Z (auto-delete-
        //   after-run) flag with /SC ONCE causes schtasks's internal V2-XML
        //   generator to require an EndBoundary element which it doesn't
        //   auto-compute from /ST alone. The fix is to use the Register-
        //   ScheduledTask cmdlet which generates a complete + valid V2 task
        //   XML (StartBoundary + ExecutionTimeLimit + DeleteExpiredTaskAfter
        //   = the auto-cleanup story without a malformed XML edge case).
        //   Caught the first time the wrapper ran on a real windows-latest
        //   GHA runner via WindowsUpgradeWrapperE2ETests.
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest

            # Inner upgrade-windows-tentacle.ps1 (placeholders pre-substituted server-side, base64-encoded)
            $InnerBase64 = '{{innerBase64}}'

            # %ProgramData%\Squid\Tentacle\upgrade — contract dir
            $DispatchDir = Join-Path $env:ProgramData 'Squid\Tentacle\upgrade'
            if (-not (Test-Path $DispatchDir)) {
                New-Item -ItemType Directory -Path $DispatchDir -Force | Out-Null
            }

            # Task Scheduler one-shot — SYSTEM identity, GUID-suffixed name for
            # cross-dispatch uniqueness. The dispatch script file is keyed to the
            # SAME GUID so two concurrent wrappers don't race on a single shared
            # dispatch.ps1 (caught by WindowsUpgradeWrapperE2ETests'
            # Wrapper_ConcurrentDispatches test — without per-task scoping, dispatch
            # B's Set-Content overwrites A's content before A's task fires, and
            # both tasks end up running B's inner).
            $TaskName = "SquidTentacleUpgrade_$([guid]::NewGuid().ToString('N'))"
            $DispatchPath = Join-Path $DispatchDir "dispatch-$TaskName.ps1"

            $InnerScript = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($InnerBase64))
            Set-Content -Path $DispatchPath -Value $InnerScript -Encoding UTF8 -Force

            Write-Host "[upgrade-wrapper] Inner script written to $DispatchPath ($($InnerScript.Length) chars)"

            # Cleanup any stale per-task dispatch files older than 1 hour. Dispatch
            # files are normally cleaned up alongside their task auto-deletion, but
            # an interrupted dispatch (host reboot mid-run) can leave an orphan
            # file. Keep operator's contract dir tidy without affecting in-flight tasks.
            try {
                Get-ChildItem -Path $DispatchDir -Filter 'dispatch-*.ps1' -ErrorAction SilentlyContinue |
                    Where-Object { $_.LastWriteTime -lt (Get-Date).AddHours(-1) } |
                    Remove-Item -Force -ErrorAction SilentlyContinue
            } catch { } # best-effort

            try {
                $action = New-ScheduledTaskAction `
                    -Execute 'powershell.exe' `
                    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$DispatchPath`""

                # Trigger NOW+2s — task fires almost immediately. We deliberately
                # don't use Start-ScheduledTask after registration because some
                # Windows configurations have a brief lag between Register and
                # the task being eligible for Start; an immediate -At trigger
                # avoids the race.
                $trigger = New-ScheduledTaskTrigger -Once -At ((Get-Date).AddSeconds(2))

                # EndBoundary is REQUIRED in V2 task XML when DeleteExpiredTaskAfter
                # is used. New-ScheduledTaskTrigger doesn't expose -EndBoundary, so
                # we set it directly. Format MUST be ISO 8601 (yyyy-MM-ddTHH:mm:ss);
                # without this, Register-ScheduledTask fails with "The task XML is
                # missing a required element or attribute" on Windows Server 2022.
                #
                # Window kept short (StartBoundary +2s, EndBoundary +30s) so the
                # trigger expires fast and Task Scheduler's auto-delete kicks in
                # quickly after the action completes. EndBoundary only governs
                # trigger validity, NOT the running action — ExecutionTimeLimit
                # (1h below) is what bounds the actual upgrade duration. Setting
                # EndBoundary to +1h would mean the task can only auto-delete
                # 1h+DeleteExpiredTaskAfter after registration which makes the
                # auto-delete invariant practically untestable + leaves stale
                # task entries visible for an hour after every upgrade.
                $trigger.EndBoundary = (Get-Date).AddSeconds(30).ToString('yyyy-MM-ddTHH:mm:ss')

                # SYSTEM identity, RunLevel Highest = elevated by default. Same
                # privilege level as the LocalSystem service tree the wrapper
                # runs in; Phase B's Stop-Service / Move-Item / Start-Service
                # rights all apply.
                $principal = New-ScheduledTaskPrincipal `
                    -UserId 'SYSTEM' `
                    -LogonType ServiceAccount `
                    -RunLevel Highest

                # DeleteExpiredTaskAfter = 30s (the documented minimum that Task
                # Scheduler honours; lower values are silently treated as "never
                # delete"). Combined with EndBoundary +30s, total wall-clock to
                # auto-delete is ≈60s after registration — fits inside the E2E
                # test's auto-delete poll window.
                $settings = New-ScheduledTaskSettingsSet `
                    -DeleteExpiredTaskAfter (New-TimeSpan -Seconds 30) `
                    -ExecutionTimeLimit (New-TimeSpan -Hours 1) `
                    -AllowStartIfOnBatteries `
                    -DontStopIfGoingOnBatteries `
                    -StartWhenAvailable

                Register-ScheduledTask `
                    -TaskName $TaskName `
                    -Action $action `
                    -Trigger $trigger `
                    -Principal $principal `
                    -Settings $settings `
                    -Force | Out-Null
            } catch {
                Write-Host "::error:: Register-ScheduledTask failed for task '$TaskName': $($_.Exception.Message)"
                exit 1
            }

            Write-Host "[upgrade-wrapper] Task '$TaskName' registered (UserId=SYSTEM, auto-deletes ≈60s after registration)"
            Write-Host "[upgrade-wrapper] Task '$TaskName' triggered; Phase A+B run detached as SYSTEM. Outcome via last-upgrade.json on next health check."
            exit 0
            """;
    }

    /// <summary>
    /// Canonical Windows zip URL — same pattern as Linux's tarball except
    /// for the file extension. <c>{RID}</c> placeholder is rewritten on the
    /// agent side via PowerShell's <c>$RID</c> variable, so the server stays
    /// agent-arch agnostic.
    /// </summary>
    internal static string BuildDownloadUrl(string version, string rid)
    {
        var baseUrl = ResolveDownloadBaseUrl();

        return $"{baseUrl}/{version}/squid-tentacle-{version}-{rid}.zip";
    }

    internal static string ResolveDownloadBaseUrl()
    {
        var raw = System.Environment.GetEnvironmentVariable(DownloadBaseUrlEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return DefaultDownloadBaseUrl;

        var normalized = raw.Trim().TrimEnd('/');

        // Mirror Linux: warn-not-reject for non-HTTPS overrides — air-gapped
        // internal HTTP mirrors are a legitimate operator pattern, but we
        // want a security trail in logs.
        if (!normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            Log.Warning(
                "[Upgrade] {EnvVar} is set to a non-HTTPS URL ({Url}). Zip downloads are " +
                "vulnerable to MITM tampering on untrusted networks. Use HTTPS unless this is " +
                "an air-gapped internal mirror you fully trust.",
                DownloadBaseUrlEnvVar, normalized);

        return normalized;
    }

    /// <summary>
    /// Resolves the local-agent healthcheck URL the .ps1 polls after the
    /// service restart. Same defaults as Linux (port 8080, /healthz path)
    /// because the Tentacle's HealthCheckServer is OS-agnostic.
    /// </summary>
    internal static string ResolveHealthcheckUrl()
    {
        var raw = System.Environment.GetEnvironmentVariable(HealthcheckUrlEnvVar);

        return string.IsNullOrWhiteSpace(raw) ? DefaultHealthcheckUrl : raw.Trim().TrimEnd('/');
    }

    /// <summary>
    /// Resolves the post-restart healthcheck failure mode. Default
    /// (env unset / empty) is <c>false</c> — warning + proceed (matches
    /// Octopus Tentacle). When the env var parses as a recognised
    /// truthy value (case-insensitive: <c>true</c> / <c>1</c> /
    /// <c>yes</c> / <c>on</c>), strict mode is enabled — healthcheck
    /// timeout triggers Invoke-Rollback. Any other value falls back to
    /// the default with a structured warning so operator typos surface
    /// in logs.
    /// </summary>
    internal static bool ResolveHealthcheckFatal()
    {
        var raw = System.Environment.GetEnvironmentVariable(HealthcheckFatalEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return false;

        var normalized = raw.Trim().ToLowerInvariant();

        if (normalized is "true" or "1" or "yes" or "on") return true;
        if (normalized is "false" or "0" or "no" or "off") return false;

        Log.Warning(
            "[Upgrade] {EnvVar} is set to an unrecognised value ('{Value}'). " +
            "Recognised truthy: 'true' / '1' / 'yes' / 'on'. " +
            "Recognised falsy:  'false' / '0' / 'no' / 'off'. " +
            "Falling back to default (false — warning + proceed on healthcheck timeout).",
            HealthcheckFatalEnvVar, raw);
        return false;
    }

    /// <summary>
    /// Resolves the SCM <c>WaitForStatus</c> wall-clock cap. Defaults to
    /// <see cref="DefaultServiceTimeoutSeconds"/> (30s). Invalid values
    /// (negative, non-numeric) silently fall back to default — operator-
    /// friendly: a typo'd env var doesn't break upgrades.
    /// </summary>
    internal static int ResolveServiceTimeoutSeconds()
    {
        var raw = System.Environment.GetEnvironmentVariable(ServiceTimeoutSecondsEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return DefaultServiceTimeoutSeconds;

        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1)
        {
            Log.Warning(
                "[Upgrade] {EnvVar} is set to an invalid value ('{Value}'). " +
                "Must be a positive integer (seconds). " +
                "Falling back to default of {Default}.",
                ServiceTimeoutSecondsEnvVar, raw, DefaultServiceTimeoutSeconds);
            return DefaultServiceTimeoutSeconds;
        }

        return parsed;
    }

    /// <summary>
    /// Resolves the post-restart healthcheck poll count. Defaults to
    /// <see cref="DefaultHealthcheckRetries"/> (30 × 2s = 60s window).
    /// Invalid values (negative, non-numeric) silently fall back to default
    /// — operator-friendly: a typo'd env var doesn't break upgrades, just
    /// uses the safe default.
    /// </summary>
    internal static int ResolveHealthcheckRetries()
    {
        var raw = System.Environment.GetEnvironmentVariable(HealthcheckRetriesEnvVar);

        if (string.IsNullOrWhiteSpace(raw)) return DefaultHealthcheckRetries;

        if (!int.TryParse(raw.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1)
        {
            Log.Warning(
                "[Upgrade] {EnvVar} is set to an invalid value ('{Value}'). " +
                "Must be a positive integer (number of 2-second poll attempts). " +
                "Falling back to default of {Default}.",
                HealthcheckRetriesEnvVar, raw, DefaultHealthcheckRetries);
            return DefaultHealthcheckRetries;
        }

        return parsed;
    }

    // ── Embedded script loading ──────────────────────────────────────────────

    private static string LoadEmbeddedScript()
    {
        var asm = Assembly.GetExecutingAssembly();

        using var stream = asm.GetManifestResourceStream(EmbeddedScriptResource)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedScriptResource}' not found. " +
                "Verify Squid.Core.csproj has <EmbeddedResource Include=\"Resources\\Upgrade\\*\" />.");

        using var reader = new StreamReader(stream);

        return reader.ReadToEnd();
    }

    private static MachineUpgradeOutcome Failed(string detail) => new()
    {
        Status = MachineUpgradeStatus.Failed,
        Detail = detail,
        AgentVersionMayHaveChanged = false
    };
}
