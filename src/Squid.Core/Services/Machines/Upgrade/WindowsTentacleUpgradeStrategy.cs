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
/// P1-Phase12.E.4 — registered counterpart to <see cref="LinuxTentacleUpgradeStrategy"/>
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
/// the rendered inner template to <c>%ProgramData%\Squid\Tentacle\upgrade\dispatch.ps1</c>,
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
    /// Default install dir matches Phase 12.E.0's <c>install-tentacle.ps1</c>
    /// — operators upgrading a deployment that used the canonical installer
    /// don't need to override. Pinned per Rule 8 by
    /// <c>WindowsTentacleUpgradeStrategyTests.DefaultInstallDir_PinnedToCanonicalProgramFilesPath</c>.
    /// </summary>
    internal const string DefaultInstallDir = @"C:\Program Files\Squid Tentacle";

    /// <summary>
    /// Default service name matches what <c>WindowsServiceHost.Install</c>
    /// (Phase 12.C) creates and what <c>install-tentacle.ps1</c> registers.
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

    private const string DefaultDownloadBaseUrl = "https://github.com/SolarifyDev/Squid/releases/download";
    private const string DefaultHealthcheckUrl = "http://127.0.0.1:8080/healthz";

    /// <summary>
    /// Wall-clock cap for a single upgrade dispatch. Mirrors Linux's
    /// 5-minute cap — must stay strictly less than
    /// <c>MachineUpgradeService.LockExpiry</c> (7 min) so an abandoned
    /// dispatch can't hold the Redis lock past TTL.
    /// </summary>
    internal static readonly TimeSpan UpgradeScriptTimeout = TimeSpan.FromMinutes(5);

    private static readonly Lazy<string> _scriptTemplate = new(LoadEmbeddedScript);

    /// <summary>
    /// Default method order — zip-only in Phase 12.E.4. Phase 12.E.5 will
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

        // P1-Phase12.E.5 — read via the centralized IsWindows property
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
            .Replace("{{INSTALL_METHODS}}", installMethodsBlock, StringComparison.Ordinal);
    }

    /// <summary>
    /// Constructs the Halibut-connected outer wrapper. Decodes the base64
    /// inner content, writes it to <c>%ProgramData%\Squid\Tentacle\upgrade\dispatch.ps1</c>,
    /// schedules a one-shot Task Scheduler task as SYSTEM (cross-dispatch
    /// uniqueness via GUID-suffixed task name), triggers it, then exits 0.
    /// Inner script runs in a separate Task Scheduler process tree —
    /// survives Phase B's <c>Stop-Service</c>.
    ///
    /// <para><b>Why base64 not a here-string</b>: PowerShell here-strings
    /// (<c>@'...'@</c>) terminate at <c>'@</c> at start of line. A future
    /// install method snippet (<c>MsiUpgradeMethod</c>, etc.) might happen
    /// to include such a sequence in a heredoc-style log message. Base64
    /// encoding makes the inner content opaque to the wrapper's lexer.
    /// Operators can still inspect the inner via <c>%ProgramData%\Squid\Tentacle\upgrade\dispatch.ps1</c>
    /// after dispatch. Audit pre-12.E.4.</para>
    /// </summary>
    internal static string BuildOuterWrapper(string innerBase64)
    {
        // C# 11 raw-string interpolation with $$ — wrap interpolations in {{ }}
        // (two each side) so PowerShell's literal `{` and `}` braces don't
        // need escaping. Result is operator-readable plain PowerShell.
        return $$"""
            $ErrorActionPreference = 'Stop'
            Set-StrictMode -Version Latest

            # Inner upgrade-windows-tentacle.ps1 (placeholders pre-substituted server-side, base64-encoded)
            $InnerBase64 = '{{innerBase64}}'

            # %ProgramData%\Squid\Tentacle\upgrade — Phase 12.A.2 contract dir
            $DispatchDir = Join-Path $env:ProgramData 'Squid\Tentacle\upgrade'
            if (-not (Test-Path $DispatchDir)) {
                New-Item -ItemType Directory -Path $DispatchDir -Force | Out-Null
            }
            $DispatchPath = Join-Path $DispatchDir 'dispatch.ps1'

            $InnerScript = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String($InnerBase64))
            Set-Content -Path $DispatchPath -Value $InnerScript -Encoding UTF8 -Force

            Write-Host "[upgrade-wrapper] Inner script written to $DispatchPath ($($InnerScript.Length) chars)"

            # Task Scheduler one-shot — SYSTEM identity, GUID-suffixed name
            # for cross-dispatch uniqueness, /Z auto-deletes after run.
            # /SC ONCE /ST is a schtasks formality; /Run immediately overrides.
            $TaskName = "SquidTentacleUpgrade_$([guid]::NewGuid().ToString('N'))"
            $createArgs = @(
                '/Create',
                '/TN', $TaskName,
                '/TR', "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$DispatchPath`"",
                '/SC', 'ONCE',
                '/ST', '23:59',
                '/RU', 'SYSTEM',
                '/F',
                '/Z'
            )

            $null = & schtasks.exe @createArgs
            if ($LASTEXITCODE -ne 0) {
                Write-Host "::error:: schtasks /Create failed for task '$TaskName' (exit $LASTEXITCODE)"
                exit 1
            }

            Write-Host "[upgrade-wrapper] Task '$TaskName' registered (RU=SYSTEM, /Z auto-deletes after run)"

            $null = & schtasks.exe /Run /TN $TaskName
            if ($LASTEXITCODE -ne 0) {
                Write-Host "::error:: schtasks /Run failed for task '$TaskName' (exit $LASTEXITCODE)"
                & schtasks.exe /Delete /TN $TaskName /F 2>&1 | Out-Null
                exit 1
            }

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
