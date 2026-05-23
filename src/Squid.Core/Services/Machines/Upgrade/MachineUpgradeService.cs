using System.Diagnostics;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;
using Squid.Message.Enums;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines.Upgrade;

public sealed class MachineUpgradeService : IMachineUpgradeService
{
    /// <summary>
    /// Redis lock TTL. Kept intentionally SHORT relative to worst-case
    /// strategy runtime because <c>RedLockNet</c> auto-extends the lock in
    /// a background timer at <c>expiry/3</c> intervals while the lock is
    /// held (the wrapped <c>RunStrategyAsync</c> is running). So for an
    /// ALIVE dispatch, the lock persists indefinitely; for a DEAD dispatch
    /// (server pod crash / OOM kill / network partition killing the
    /// process), the lock expires in at most <c>LockExpiry</c>.
    ///
    /// <para><b>Why 7 min, not 20:</b> this is the <b>abandoned-lock
    /// recovery window</b>. A crashed server mid-dispatch previously left
    /// its Redis lock alive for 20 min — operators had to wait 20 min or
    /// `DEL` the key manually before any retry could proceed. 7 min keeps
    /// the recovery short while giving enough headroom that a transient
    /// network blip between server and Redis (say, 30-60s) can't make
    /// RedLockNet's auto-extend miss two cycles and lose the lock
    /// mid-dispatch. (Auto-extend interval = 7/3 ≈ 2.3 min; two missed
    /// cycles = ~4.7 min &lt; 7 min.)</para>
    ///
    /// <para><b>Invariant (audit H-15, revised 1.5.0):</b> MUST be
    /// <c>&gt;= 2× (UpgradeScriptTimeout + 30s)</c> so a single operation
    /// can complete even if RedLockNet misses one extend cycle. Currently
    /// <c>UpgradeScriptTimeout = 5 min</c> so minimum 11 min — **but**
    /// this invariant ONLY applies if auto-extend is disabled. With
    /// RedLockNet's auto-extend ON (default), the TRUE minimum is
    /// <c>&gt;= 2× auto-extend-interval = 2× (LockExpiry/3)</c>, which is
    /// trivially satisfied for any positive TimeSpan. We keep the 7-min
    /// floor for defence-in-depth. Pinned by
    /// <c>LockExpiry_BalancedForAbandonedLockRecoveryAndAutoExtendHeadroom</c>.</para>
    /// </summary>
    internal static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(7);

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound of the upgrade progress-polling window (1.6.x fix for
    /// "events endpoint has no real-time data during the upgrade" UX
    /// gap). During this window, health checks fire at <see cref="UpgradePollingIntervalSeconds"/>
    /// intervals — each one re-captures the agent's upgrade-events JSONL +
    /// Phase B log via Capabilities RPC, populating the server-side
    /// timeline store so the FE's 2-3s polling sees near-real-time
    /// progress.
    ///
    /// <para>Sized to cover the FULL typical Phase A + Phase B + Halibut
    /// reconnect sequence. Empirical timing from local end-to-end tests:
    /// click → Phase A start: ~1s; Phase A download + method-selected:
    /// 30s (apt path); Phase B binary swap: 1s; systemctl restart +
    /// tentacle reconnect: 30-35s; healthz-pass + success: at ~T+63s.
    /// Setting the window to 90s (30 × 3s jobs) comfortably covers this
    /// including the reconnect jitter variance, with 30 × ~200ms ≈ 6s
    /// of worst-case wasted Capabilities RPCs — negligible.</para>
    ///
    /// <para>The 45s predecessor was chosen assuming the typical sequence
    /// ended at ~T+40s — that held for tarball install but apt install
    /// has ~20s extra in package-download + dpkg-runtime that pushes
    /// terminal events to T+60-65s, past the window. Symptom: UI showed
    /// "stalled" even though agent was healthy — FE never saw the
    /// terminal `success` event because server's events store stopped
    /// refreshing at T+45s and the subsequent manual health check was
    /// 30s later (FE's post-Initiated setTimeout).</para>
    /// </summary>
    internal const int UpgradePollingWindowSeconds = 90;

    /// <summary>
    /// Interval between health checks during the upgrade polling window.
    /// 3s matches the FE integration guide's recommended poll cadence —
    /// UI sees events arrive within one FE poll cycle after they're
    /// emitted on the agent.
    ///
    /// <para>Job count = window / interval = 90 / 3 = 30 Hangfire jobs
    /// per upgrade. Each is a quick Capabilities RPC (~20ms on local,
    /// ~200ms over WAN). Upper-bound ~6s of CPU time across all 30
    /// jobs, negligible.</para>
    /// </summary>
    internal const int UpgradePollingIntervalSeconds = 3;

    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IMachineRuntimeCapabilitiesCache _runtimeCache;
    private readonly IMachineRuntimeCapabilitiesPersistence _runtimePersistence;
    private readonly ITentacleVersionRegistry _versionRegistry;
    private readonly IEnumerable<IMachineUpgradeStrategy> _strategies;
    private readonly IRedisSafeRunner _redisLock;
    private readonly ISquidBackgroundJobClient _backgroundJobClient;
    private readonly IUpgradeDispatchMetadataStore _dispatchMetadata;

    public MachineUpgradeService(IMachineDataProvider machineDataProvider, IMachineRuntimeCapabilitiesCache runtimeCache, ITentacleVersionRegistry versionRegistry, IEnumerable<IMachineUpgradeStrategy> strategies, IRedisSafeRunner redisLock, ISquidBackgroundJobClient backgroundJobClient, IMachineRuntimeCapabilitiesPersistence runtimePersistence = null, IUpgradeDispatchMetadataStore dispatchMetadata = null)
    {
        _machineDataProvider = machineDataProvider;
        _runtimeCache = runtimeCache;
        _runtimePersistence = runtimePersistence;
        _versionRegistry = versionRegistry;
        _strategies = strategies;
        _redisLock = redisLock;
        _backgroundJobClient = backgroundJobClient;
        _dispatchMetadata = dispatchMetadata;
    }

    public async Task<UpgradeMachineResponseData> UpgradeAsync(UpgradeMachineCommand command, CancellationToken ct)
    {
        // Audit trail wrapper (audit A7). Every upgrade attempt — success,
        // rejected, or exception — leaves two Seq log lines with the
        // `[UpgradeAudit]` prefix for ops filtering. Outcome log uses
        // structured props (status, elapsedMs) so it's aggregate-queryable.
        // ICurrentUser integration + log-sink-based contract tests are
        //  work; today the logs capture what we know without
        // expanding the service's dependencies.
        var sw = Stopwatch.StartNew();
        UpgradeMachineResponseData result = null;

        Log.Information(
            "[UpgradeAudit] Upgrade request machineId={MachineId} targetVersion={TargetVersion} allowDowngrade={AllowDowngrade}",
            command.MachineId,
            string.IsNullOrWhiteSpace(command.TargetVersion) ? "<auto>" : command.TargetVersion.Trim(),
            command.AllowDowngrade);

        try
        {
            result = await RunUpgradeAsync(command, ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            // Legitimate user / shutdown cancel — not an error, just propagate.
            // The outcome log in the finally block records status=Exception,
            // which is accurate: no response was built.
            throw;
        }
        catch (Exception ex)
        {
            // Round-4 audit B2: ensure the exception type + message appears
            // in the audit trail filtered by `[UpgradeAudit]` — otherwise
            // ops must correlate with a generic GlobalExceptionFilter log
            // or stderr, which is painful at scale.
            Log.Error(ex, "[UpgradeAudit] Upgrade threw for machineId={MachineId}: {ExceptionType}: {ExceptionMessage}",
                command.MachineId, ex.GetType().Name, ex.Message);
            throw;
        }
        finally
        {
            Log.Information(
                "[UpgradeAudit] Upgrade outcome machineId={MachineId} machineName={MachineName} status={Status} elapsedMs={ElapsedMs} currentVersion={CurrentVersion} targetVersion={TargetVersion} detail={Detail}",
                command.MachineId,
                result?.MachineName ?? "<unknown>",
                result?.Status.ToString() ?? "Exception",
                sw.ElapsedMilliseconds,
                result?.CurrentVersion ?? "<unknown>",
                result?.TargetVersion ?? "<unknown>",
                result?.Detail ?? "<exception propagated; see prior error log>");
        }
    }

    private async Task<UpgradeMachineResponseData> RunUpgradeAsync(UpgradeMachineCommand command, CancellationToken ct)
    {
        var machine = await LoadMachineAsync(command.MachineId, ct).ConfigureAwait(false);
        var style = ReadCommunicationStyle(machine);

        // Empty style = malformed / incomplete endpoint JSON. NotSupported
        // with empty-quoted detail ("...style '' not registered") would be
        // confusing; tell the operator their registration is the actual
        // problem. Distinct from "style present but no strategy" below.
        if (string.IsNullOrWhiteSpace(style))
            return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.Failed,
                $"Machine '{machine.Name}' endpoint JSON is missing or malformed — no CommunicationStyle field found. " +
                $"Verify the machine registration is complete (re-run the registration flow, or fix the Endpoint JSON in the DB).");

        // Strategy first — if no transport can upgrade this style there is
        // no point burning a Docker Hub round-trip on a version we will
        // never use, and "NotSupported with style name" is a far more
        // actionable error than "couldn't resolve version, set env var X"
        // (which would be nonsense advice for an Ssh target).
        //
        // pass cached capabilities so the resolver can
        // route by OS (Linux vs Windows tentacles share the same wire-protocol
        // CommunicationStyle values; the OS reported by the agent's last
        // health check is what differentiates them).
        var capabilities = _runtimeCache.TryGet(machine.Id) ?? MachineRuntimeCapabilities.Empty;

        // H5 — cold-cache short-circuit for tentacle styles. Mirrors the
        // identical guard in EvaluateUpgradeEligibility (H1) so direct API
        // callers (curl, scripted automation that bypassed the UI's
        // upgrade-info gate) can't dispatch with empty OS — pre-H5 they
        // would have hit LinuxTentacleUpgradeStrategy's historical-default
        // "claim Unknown" path and dispatched the Linux upgrade script
        // against a possibly-Windows machine. H5 removes that historical
        // default in LinuxTentacleUpgradeStrategy.CanHandle AND blocks
        // the dispatch entry point here so the failure mode is impossible
        // from BOTH directions.
        if (IsTentacleStyle(style) && string.IsNullOrWhiteSpace(capabilities.Os))
            return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.Failed,
                $"Machine '{machine.Name}' OS is not yet detected (capability cache is cold). " +
                "Trigger an active health check (POST /api/machines/{id}/health-check or click 'Health Check' in the UI) " +
                "BEFORE retrying the upgrade. Pre-H5 this would have routed to the Linux upgrade strategy via a " +
                "historical-default fallback, which broke when the agent was actually Windows.");

        var strategy = ResolveStrategy(style, capabilities);

        if (strategy == null)
            return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.NotSupported, $"No upgrade strategy registered for CommunicationStyle '{style}'.");

        var targetVersion = await ResolveTargetVersionAsync(command, style, capabilities, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(targetVersion)) return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.Failed, NoTargetVersionDetail());

        // Strict semver gate — both operator-supplied AND auto-resolved versions
        // pass through this single boundary BEFORE reaching the bash template
        // or the Halibut payload. Closes the bash-injection surface (audit H-5)
        // and rejects malformed tags like "1.4" / "1.4.0.0" / "latest" that
        // would produce broken download URLs (audit H-4).
        if (!SemVer.TryParse(targetVersion, out var parsedTarget))
            return BuildResponse(machine, currentVersion: null, targetVersion: targetVersion, MachineUpgradeStatus.Failed,
                $"Target version '{targetVersion}' is not valid semver (MAJOR.MINOR.PATCH[-pre][+build]). Refusing to dispatch — would generate an invalid download URL or shell-unsafe payload.");

        var currentVersion = ReadCachedAgentVersion(machine.Id);
        var relation = CompareVersions(currentVersion, parsedTarget);

        if (relation == VersionRelation.UpToDate)
            return BuildResponse(machine, currentVersion, parsedTarget.Raw, MachineUpgradeStatus.AlreadyUpToDate,
                $"Machine '{machine.Name}' already on version {currentVersion}; nothing to do.");

        if (relation == VersionRelation.WouldBeDowngrade && !command.AllowDowngrade)
            return BuildResponse(machine, currentVersion, parsedTarget.Raw, MachineUpgradeStatus.AlreadyUpToDate,
                $"Machine '{machine.Name}' current version {currentVersion} is higher than requested {parsedTarget.Raw}. " +
                "Downgrades are refused by default. Pass AllowDowngrade=true in the request body to force (intended for emergency revert scenarios).");

        return await DispatchUnderLockAsync(machine, strategy, parsedTarget.Raw, currentVersion, ct).ConfigureAwait(false);
    }

    // ── Read-only upgrade-info probe for the FE's per-row badge (§9.2) ──────

    public async Task<GetUpgradeInfoResponseData> GetUpgradeInfoAsync(GetUpgradeInfoRequest request, CancellationToken ct)
    {
        var machine = await LoadMachineAsync(request.MachineId, ct).ConfigureAwait(false);
        var style = ReadCommunicationStyle(machine);
        var currentVersion = ReadCachedAgentVersion(machine.Id);

        // capabilities thread through info path so OS-aware
        // resolver can pick the right strategy (Linux vs Windows tentacle).
        // H1: Empty/cold cache no longer silently routes to Linux historical
        // default — EvaluateUpgradeEligibility short-circuits with NoOsDetected
        // for tentacle styles before strategy resolution.
        var capabilities = _runtimeCache.TryGet(machine.Id) ?? MachineRuntimeCapabilities.Empty;

        var latestVersion = await ResolveLatestForInfoAsync(style, capabilities, ct).ConfigureAwait(false);

        var (canUpgrade, reasonCode, reasonMessage) = EvaluateUpgradeEligibility(style, capabilities, currentVersion, latestVersion);

        return new GetUpgradeInfoResponseData
        {
            MachineId = machine.Id,
            CurrentVersion = currentVersion ?? string.Empty,
            LatestAvailableVersion = latestVersion ?? string.Empty,
            CanUpgrade = canUpgrade,
            Reason = reasonMessage,
            ReasonCode = reasonCode
        };
    }

    /// <summary>
    /// Skip the version-registry call for styles we can't upgrade anyway —
    /// no point burning Docker Hub quota on an SSH target just to render
    /// its info row.
    /// </summary>
    private async Task<string> ResolveLatestForInfoAsync(string style, MachineRuntimeCapabilities capabilities, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(style) || ResolveStrategy(style, capabilities) == null)
            return string.Empty;

        return await _versionRegistry.GetLatestVersionAsync(style, capabilities, ct).ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>
    /// Pure decision: given what we know, is it worth showing the operator
    /// an upgrade affordance? Returns a tuple of <c>(canUpgrade, reasonCode,
    /// reasonMessage)</c>:
    /// <list type="bullet">
    ///   <item><b>canUpgrade</b> — boolean the FE uses to show/hide the upgrade button</item>
    ///   <item><b>reasonCode</b> — structured <see cref="UpgradeEligibilityReason"/> the FE can branch on (e.g. show a "Run health check" deep-link for <see cref="UpgradeEligibilityReason.NoOsDetected"/>)</item>
    ///   <item><b>reasonMessage</b> — operator-facing one-liner, safe to render verbatim in a tooltip</item>
    /// </list>
    ///
    /// <para><b>H1 — Cold-cache short-circuit (avoids misleading "Docker Hub" UX)</b>:
    /// Before H1, when the capability cache was cold (empty <c>Os</c>) for a
    /// tentacle-style machine, <see cref="LinuxTentacleUpgradeStrategy"/> claimed
    /// the request as historical default → version registry queried Docker Hub
    /// → Windows machines saw "Docker Hub unreachable, set
    /// <c>SQUID_TARGET_LINUX_TENTACLE_VERSION</c>" messages. Operators followed
    /// the wrong remediation. Now we short-circuit BEFORE strategy resolution
    /// with <see cref="UpgradeEligibilityReason.NoOsDetected"/> and a message
    /// that directs the operator to the health-check endpoint instead.</para>
    ///
    /// <para><b>H1 — OS-aware <see cref="UpgradeEligibilityReason.RegistryUnreachable"/>
    /// messages</b>: <see cref="BuildRegistryUnreachableMessage"/> picks the
    /// right registry endpoint and override env var per OS, so Windows
    /// machines see "GitHub Releases" + <c>SQUID_TARGET_WINDOWS_TENTACLE_VERSION</c>
    /// and Linux machines see "Docker Hub" + <c>SQUID_TARGET_LINUX_TENTACLE_VERSION</c>.</para>
    /// </summary>
    private (bool canUpgrade, UpgradeEligibilityReason code, string message) EvaluateUpgradeEligibility(
        string style, MachineRuntimeCapabilities capabilities, string currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(style))
            return (false, UpgradeEligibilityReason.NoCommunicationStyle,
                "Machine endpoint JSON is malformed or missing CommunicationStyle — re-run machine registration.");

        // H1: cold-cache short-circuit. For tentacle styles we cannot
        // reliably route to the right strategy / version registry without
        // OS data; rather than fall through to the Linux historical default,
        // tell the operator to run an active health check first. Non-tentacle
        // styles (Ssh, etc.) fall through to ResolveStrategy below, which
        // correctly returns null → StyleNotSupported regardless of OS.
        if (IsTentacleStyle(style) && string.IsNullOrWhiteSpace(capabilities?.Os))
            return (false, UpgradeEligibilityReason.NoOsDetected,
                "OS not yet detected for this machine — the capability cache is cold (likely a recent " +
                "server pod restart) or the agent's CapabilitiesResponse is missing the 'os' metadata field. " +
                "Trigger an active health check (POST /api/machines/{id}/health-check or click 'Health Check' " +
                "in the UI) to populate the OS metadata. If the cache stays empty AFTER a health check, " +
                "the agent is too old to report 'os' metadata — upgrade it via the manual install path " +
                "(see deploy/scripts/install-tentacle.sh / install-tentacle.ps1).");

        if (ResolveStrategy(style, capabilities) == null)
            return (false, UpgradeEligibilityReason.StyleNotSupported,
                $"CommunicationStyle '{style}' is not supported for in-UI upgrades — use the style's manual upgrade path.");

        if (string.IsNullOrEmpty(latestVersion))
            return (false, UpgradeEligibilityReason.RegistryUnreachable,
                BuildRegistryUnreachableMessage(capabilities));

        if (string.IsNullOrWhiteSpace(currentVersion))
            return (true, UpgradeEligibilityReason.EligibleCurrentVersionUnknown,
                "Current agent version is unknown (machine has not been health-checked yet). " +
                "Upgrade will dispatch; AlreadyUpToDate will catch it if the agent is already on the target.");

        if (!SemVer.TryParse(currentVersion, out var parsedCurrent) || !SemVer.TryParse(latestVersion, out var parsedLatest))
            return (true, UpgradeEligibilityReason.EligibleNonSemverComparison,
                $"Cannot compare versions strictly (non-semver current='{currentVersion}'). " +
                $"Upgrade is allowed; worst case the agent's per-version idempotency lock no-ops the run.");

        var compare = parsedCurrent.CompareTo(parsedLatest);

        if (compare < 0)
            return (true, UpgradeEligibilityReason.Eligible,
                $"Latest published version {latestVersion} is newer than current {currentVersion}.");
        if (compare == 0)
            return (false, UpgradeEligibilityReason.AlreadyUpToDate,
                $"Already on version {latestVersion}; nothing to do.");

        return (false, UpgradeEligibilityReason.WouldBeDowngrade,
            $"Current version {currentVersion} is newer than the latest published ({latestVersion}) — " +
            $"likely a pre-release or dev build. Use AllowDowngrade=true on the upgrade endpoint if a downgrade is deliberate.");
    }

    /// <summary>
    /// Whether <paramref name="style"/> is a wire-protocol tentacle style
    /// (<c>TentaclePolling</c> / <c>TentacleListening</c>). Used by the H1
    /// cold-cache short-circuit — these styles need OS data to resolve the
    /// correct upgrade strategy, and short-circuiting before
    /// <see cref="ResolveStrategy"/> avoids the misleading Linux historical
    /// default. Other styles (<c>Ssh</c>, <c>KubernetesAgent</c>) are
    /// classified independently of OS and don't need this guard.
    /// </summary>
    private static bool IsTentacleStyle(string style)
        => string.Equals(style, nameof(CommunicationStyle.TentaclePolling), StringComparison.Ordinal)
        || string.Equals(style, nameof(CommunicationStyle.TentacleListening), StringComparison.Ordinal);

    /// <summary>
    /// H1: per-OS registry-unreachable message. Replaces the pre-H1 hardcoded
    /// "Docker Hub … SQUID_TARGET_LINUX_TENTACLE_VERSION" message that
    /// misdirected Windows operators. Each branch names the exact registry
    /// endpoint, the network host they need to verify, and the OS-specific
    /// override env var.
    /// </summary>
    internal static string BuildRegistryUnreachableMessage(MachineRuntimeCapabilities capabilities)
    {
        if (capabilities != null && capabilities.IsWindows)
            return "Could not resolve the latest Windows tentacle version from " +
                   "github.com/SolarifyDev/Squid/releases — check the server's outbound HTTPS access " +
                   "(api.github.com:443). To pin a specific version offline, set " +
                   $"{TentacleVersionRegistry.WindowsOverrideEnvVar} on the server pod.";

        if (capabilities != null && capabilities.IsLinux)
            return "Could not resolve the latest Linux tentacle version from Docker Hub " +
                   "(squidcd/squid-tentacle-linux) — check the server's outbound HTTPS access " +
                   "(registry-1.docker.io:443). To pin a specific version offline, set " +
                   $"{TentacleVersionRegistry.LinuxOverrideEnvVar} on the server pod.";

        // Defensive fallback: capabilities resolved a strategy but the OS
        // predicate didn't match Windows OR Linux (e.g. KubernetesAgent style
        // with Unknown OS, or a future macOS strategy). Don't pretend to know
        // which registry to suggest — give the generic "registry unreachable"
        // signal so the operator looks at the strategy-specific docs.
        return "Could not resolve the latest tentacle version — the registry path for this " +
               "communication style is unreachable and no env-var override is set. " +
               "Check server logs for the specific registry endpoint and ops hint.";
    }

    // ── Loading + reading ────────────────────────────────────────────────────

    private async Task<Persistence.Entities.Deployments.Machine> LoadMachineAsync(int machineId, CancellationToken ct)
    {
        var machine = await _machineDataProvider.GetMachinesByIdAsync(machineId, ct).ConfigureAwait(false);

        return machine ?? throw new MachineNotFoundException(machineId);
    }

    private static string ReadCommunicationStyle(Persistence.Entities.Deployments.Machine machine)
        => EndpointJsonHelper.GetField(machine.Endpoint, "CommunicationStyle");

    private string ReadCachedAgentVersion(int machineId)
        => _runtimeCache.TryGet(machineId)?.AgentVersion ?? string.Empty;

    // ── Resolution: target version, strategy ─────────────────────────────────

    private async Task<string> ResolveTargetVersionAsync(UpgradeMachineCommand command, string style, MachineRuntimeCapabilities capabilities, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.TargetVersion)) return command.TargetVersion.Trim();

        return await _versionRegistry.GetLatestVersionAsync(style, capabilities, ct).ConfigureAwait(false);
    }

    private IMachineUpgradeStrategy ResolveStrategy(string style, MachineRuntimeCapabilities capabilities)
    {
        // Single-owner invariant. Without this, a future refactor widening
        // two strategies' CanHandle() to overlap would silently dispatch
        // to whichever Autofac registered first — the operator sees no
        // warning, gets the wrong transport (or wrong URL pattern, or
        // wrong script), and debugging is brutal. Throw loudly at the
        // first trigger before any side effect. Surfaces both class names
        // so the fix is obvious: narrow CanHandle in one of them.
        //
        // `capabilities` (cached from last health check)
        // is the OS-axis input. Linux tentacle skips Windows agents,
        // Windows tentacle claims them; the (style + OS) tuple is the
        // unique resolver key. K8s strategy ignores capabilities (single
        // OS variant). Cold cache → LinuxTentacleUpgradeStrategy claims
        // the empty-OS case as the historical default to preserve
        //  behaviour.
        var matches = _strategies.Where(s => s.CanHandle(style, capabilities)).ToList();

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Multiple upgrade strategies claim CommunicationStyle '{style}' on OS '{capabilities.Os}': " +
                $"{string.Join(", ", matches.Select(m => m.GetType().Name))}. " +
                "Each (style, OS) tuple must have exactly one owner. Narrow CanHandle() in one of them.");

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>
    /// Three-way version relationship so the orchestrator can distinguish
    /// "genuinely up-to-date" from "would be a downgrade" — the latter is
    /// only allowed via operator-explicit <see cref="UpgradeMachineCommand.AllowDowngrade"/>.
    /// Audit A5.
    /// </summary>
    private enum VersionRelation { NeedsUpgrade, UpToDate, WouldBeDowngrade }

    /// <summary>
    /// Spec-correct semver compare via <see cref="SemVer.CompareTo"/>.
    /// Cold cache (<c>current</c> blank) → <see cref="VersionRelation.NeedsUpgrade"/>
    /// so we still dispatch and let the agent be the source of truth on
    /// the next health check.
    /// </summary>
    private static VersionRelation CompareVersions(string current, SemVer target)
    {
        if (string.IsNullOrWhiteSpace(current)) return VersionRelation.NeedsUpgrade;

        // Legacy agent / non-semver dev build → conservatively dispatch.
        // Worst case is one idempotent script run, far better than silently
        // skipping a wanted upgrade because the version string confused us.
        if (!SemVer.TryParse(current, out var parsedCurrent)) return VersionRelation.NeedsUpgrade;

        var c = parsedCurrent.CompareTo(target);

        if (c == 0) return VersionRelation.UpToDate;

        return c > 0 ? VersionRelation.WouldBeDowngrade : VersionRelation.NeedsUpgrade;
    }

    // ── Lock + dispatch + cache invalidation ─────────────────────────────────

    private async Task<UpgradeMachineResponseData> DispatchUnderLockAsync(Persistence.Entities.Deployments.Machine machine, IMachineUpgradeStrategy strategy, string targetVersion, string currentVersion, CancellationToken ct)
    {
        // Key format MUST match UpgradeDispatchLockReconciler.BuildLockKey —
        // pinned by LockKey_MatchesMachineUpgradeServiceFormat.
        var lockKey = UpgradeDispatchLockReconciler.BuildLockKey(machine.Id);

        // Three outcomes after the 1.6.x RedisSafeRunner refactor (audit P0-F3):
        //   - Non-null result: strategy ran to completion under the lock
        //   - Null result: contention (another caller holds the lock)
        //   - LockAcquireFailedException: infrastructure failure (Redis down,
        //     network partition, etc.) — distinguishable from contention so
        //     operators don't get misled into retrying against broken infra.
        UpgradeMachineResponseData result;
        try
        {
            result = await _redisLock.ExecuteWithLockAsync<UpgradeMachineResponseData>(
                lockKey,
                () => RunStrategyWithMetadataAsync(machine, strategy, targetVersion, currentVersion, ct),
                expiry: LockExpiry, wait: LockWait, retry: LockRetry).ConfigureAwait(false);
        }
        catch (LockAcquireFailedException ex)
        {
            // Infrastructure failure — surface as a distinct error so the
            // operator understands retrying won't help until ops recovers
            // Redis. Pre-fix this was indistinguishable from contention,
            // masking Redis outages as "another user is upgrading".
            return BuildResponse(machine, currentVersion, targetVersion, MachineUpgradeStatus.Failed,
                $"Upgrade dispatch could not acquire the distributed lock due to Redis " +
                $"infrastructure failure ({ex.InnerException?.GetType().Name ?? "unknown"}: " +
                $"{ex.InnerException?.Message ?? "no detail"}). No upgrade script was sent " +
                "to the agent. This indicates a server-side infrastructure problem (Redis " +
                "unreachable, TLS handshake refused, network partition) — retry once the " +
                "infrastructure recovers, or contact your operator if it persists.");
        }

        if (result != null) return result;

        // H4 — contention path. Pre-H4 we returned a hardcoded "wait 2 min and
        // retry" message; operator had no idea WHEN the original dispatch
        // started or what version was targeted (and "2 min" was wrong — the
        // worst-case wait is LockExpiry = 7 min). Now we read the companion
        // metadata key and tell the operator exactly when the in-flight
        // dispatch began + when to expect completion.
        return BuildResponse(machine, currentVersion, targetVersion, MachineUpgradeStatus.Failed,
            await BuildContentionMessageAsync(machine, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// H4 — wrap <see cref="RunStrategyAsync"/> with metadata write-before /
    /// delete-after-success. The metadata key lives at
    /// <c>{lockKey}:meta</c> and shares the lock's TTL, so a crashed
    /// dispatcher's metadata is auto-cleaned by Redis (no leak). On lock
    /// release path the explicit DeleteAsync is best-effort — if it fails
    /// the TTL catches it.
    /// </summary>
    private async Task<UpgradeMachineResponseData> RunStrategyWithMetadataAsync(Persistence.Entities.Deployments.Machine machine, IMachineUpgradeStrategy strategy, string targetVersion, string currentVersion, CancellationToken ct)
    {
        if (_dispatchMetadata != null)
        {
            try
            {
                await _dispatchMetadata.WriteAsync(machine.Id, new UpgradeDispatchMetadata
                {
                    DispatchedAt = DateTimeOffset.UtcNow,
                    TargetVersion = targetVersion,
                    CurrentVersion = currentVersion ?? string.Empty
                }, LockExpiry, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Metadata is best-effort — log + continue. The lock is still
                // held and the upgrade proceeds; the contention UX just falls
                // back to the old generic message.
                Log.Warning(ex,
                    "Failed to write upgrade dispatch metadata for machine {MachineId}. " +
                    "Upgrade proceeds; contention UX falls back to generic message.",
                    machine.Id);
            }
        }

        try
        {
            return await RunStrategyAsync(machine, strategy, targetVersion, currentVersion, ct).ConfigureAwait(false);
        }
        finally
        {
            if (_dispatchMetadata != null)
            {
                try
                {
                    await _dispatchMetadata.DeleteAsync(machine.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Best-effort delete; TTL catches stragglers.
                    Log.Debug(ex,
                        "Failed to delete upgrade dispatch metadata for machine {MachineId} after dispatch release. " +
                        "Redis TTL will clean it up within {Ttl}.",
                        machine.Id, LockExpiry);
                }
            }
        }
    }

    /// <summary>
    /// H4 — build the contention error message. If H4's metadata store has a
    /// record of when the in-flight dispatch started, embed it so the operator
    /// can wait an INFORMED amount of time. Falls back to the pre-H4 generic
    /// message when metadata is unavailable (Redis hiccup, race, etc.).
    /// </summary>
    private async Task<string> BuildContentionMessageAsync(Persistence.Entities.Deployments.Machine machine, CancellationToken ct)
    {
        if (_dispatchMetadata == null)
            return $"Machine '{machine.Name}' is currently being upgraded by another request. " +
                   "Wait for it to complete (typically under 2 minutes) and retry.";

        UpgradeDispatchMetadata metadata;
        try
        {
            metadata = await _dispatchMetadata.ReadAsync(machine.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "Failed to read upgrade dispatch metadata for machine {MachineId}. " +
                "Contention message falls back to generic shape.",
                machine.Id);
            metadata = null;
        }

        if (metadata == null)
            return $"Machine '{machine.Name}' is currently being upgraded by another request. " +
                   $"Wait for it to complete (at most {LockExpiry.TotalMinutes:F0} minutes from start) and retry.";

        var elapsed = DateTimeOffset.UtcNow - metadata.DispatchedAt;
        var deadline = metadata.DispatchedAt + LockExpiry;
        var remaining = deadline - DateTimeOffset.UtcNow;

        // 1.8.0 hardening audit followup — clock-skew guard. Two server pods
        // can disagree on UTC by seconds-to-minutes during NTP slew / fresh
        // pod startup before time-sync converges. If pod B reads metadata
        // pod A wrote, B can see DispatchedAt in B's future → elapsed turns
        // negative and the operator sees the cosmetically wrong "~-3599s ago"
        // in the contention message. `remaining` was already clamped at
        // construction-time of H4; `elapsed` needed the same treatment.
        return $"Machine '{machine.Name}' is currently being upgraded by another request " +
               $"(dispatched at {metadata.DispatchedAt:HH:mm:ss} UTC, ~{Math.Max(0, elapsed.TotalSeconds):F0}s ago, " +
               $"targeting {metadata.TargetVersion ?? "unknown"}). " +
               $"Expected to complete by {deadline:HH:mm:ss} UTC " +
               $"(~{Math.Max(0, remaining.TotalSeconds):F0}s remaining). " +
               "Retry after that, or contact your operator if it appears genuinely stuck.";
    }

    private async Task<UpgradeMachineResponseData> RunStrategyAsync(Persistence.Entities.Deployments.Machine machine, IMachineUpgradeStrategy strategy, string targetVersion, string currentVersion, CancellationToken ct)
    {
        // Schedule rapid-polling health checks BEFORE the strategy dispatch so
        // the server-side upgrade-events timeline fills up during the observer
        // wait (50–180s for LinuxTentacle — scope detach + systemctl restart +
        // Halibut reconnect). Previously this was called AFTER the dispatch
        // returned, meaning the FE polled /upgrade-events and saw "empty" for
        // the entire observer-wait window — looked exactly like a broken API
        // even though the agent was actively running the upgrade script.
        //
        // Fire-and-forget via Hangfire — no observable side effect if the
        // strategy then fails-fast at validation (e.g. invalid target version).
        // Worst case is a burst of 15 cheap Capabilities RPCs against a
        // machine that didn't actually upgrade; trivially tolerable.
        ScheduleRapidPolling(machine.Id);

        var outcome = await strategy.UpgradeAsync(machine, targetVersion, ct).ConfigureAwait(false);

        InvalidateCacheIfChanged(machine.Id, outcome);

        return BuildResponse(machine, currentVersion, targetVersion, outcome.Status, outcome.Detail);
    }

    /// <summary>
    /// Drop the cached agent version when the strategy says the binary may
    /// have changed AND schedule a delayed health check (1.6.x fix) so the
    /// cache gets REFRESHED (not just emptied) before the next scheduled
    /// health check — without the scheduled refresh, UI showed the agent
    /// as "unknown version" for up to the health-check interval (hours
    /// by default) and operators had to manually click "Run health check"
    /// to confirm the upgrade landed.
    ///
    /// <para>The scheduled refresh fires after
    /// <see cref="PostUpgradeHealthCheckDelay"/> (45s), giving the agent
    /// time to finish Phase B (binary swap + systemctl restart + Halibut
    /// reconnect). Hangfire re-scopes, so the health check runs with a
    /// fresh DI container — safe even though this service is scoped.</para>
    ///
    /// <para>Outcome-driven (audit N-6): each strategy explicitly sets
    /// <see cref="MachineUpgradeOutcome.AgentVersionMayHaveChanged"/> based on
    /// what its dispatch path actually does. A new
    /// <see cref="MachineUpgradeStatus"/> value can't accidentally miss
    /// invalidation because the orchestrator never inspects the enum here.</para>
    /// </summary>
    private void InvalidateCacheIfChanged(int machineId, MachineUpgradeOutcome outcome)
    {
        if (!outcome.AgentVersionMayHaveChanged) return;

        _runtimeCache.Invalidate(machineId);

        // H2 — also NULL out the persisted snapshot so the next server pod
        // restart doesn't hydrate the stale pre-upgrade version. Fire-and-forget
        // with logging — if the DB write fails, the in-memory invalidation is
        // already done and the next successful health check will overwrite the
        // stale row with current data.
        if (_runtimePersistence == null) return;

        _ = Task.Run(async () =>
        {
            try
            {
                await _runtimePersistence.InvalidateAsync(machineId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "Failed to invalidate persisted runtime capabilities for machine {MachineId} after upgrade. " +
                    "In-memory cache is cleared; next health check will overwrite the stale DB row.",
                    machineId);
            }
        });
    }

    /// <summary>
    /// Schedules the rapid-polling burst that populates the server-side
    /// upgrade-events timeline during the observer wait window. Called
    /// BEFORE strategy dispatch so the FE's /upgrade-events polling sees
    /// live data (vs. "empty" for 50–180s while observer waits for the
    /// restart-disconnected script to re-report).
    ///
    /// <para>Each scheduled job is a Capabilities RPC that re-reads the
    /// agent's upgrade-events JSONL + last-upgrade.json + Phase B log.
    /// Jobs fire every <see cref="UpgradePollingIntervalSeconds"/> seconds
    /// for <see cref="UpgradePollingWindowSeconds"/> seconds total.</para>
    ///
    /// <para>Fire-and-forget: errors here must not bubble back to the
    /// operator's HTTP request (a failed observability-poll is cosmetic).
    /// Try/catch guards against Hangfire storage exceptions (Redis down etc).</para>
    ///
    /// <para>Early-completion: if the upgrade finishes in 10s, the remaining
    /// jobs still fire — each is a cheap no-op Capabilities RPC that reads
    /// the same terminal state. ~200ms × 15 jobs = 3s of wasted work —
    /// tolerable for the UX win.</para>
    /// </summary>
    private void ScheduleRapidPolling(int machineId)
    {
        try
        {
            for (var seconds = UpgradePollingIntervalSeconds; seconds <= UpgradePollingWindowSeconds; seconds += UpgradePollingIntervalSeconds)
            {
                _backgroundJobClient.Schedule<IMachineHealthCheckService>(
                    svc => svc.ManualHealthCheckAsync(machineId, CancellationToken.None),
                    TimeSpan.FromSeconds(seconds));
            }

            Log.Information(
                "[UpgradeAudit] Scheduled {JobCount} rapid health checks for machine {MachineId} at {Interval}s intervals over {Window}s window",
                UpgradePollingWindowSeconds / UpgradePollingIntervalSeconds,
                machineId,
                UpgradePollingIntervalSeconds,
                UpgradePollingWindowSeconds);
        }
        catch (Exception ex)
        {
            Log.Warning(ex,
                "[UpgradeAudit] Failed to schedule upgrade-progress polling for machine {MachineId} — " +
                "UI will show stale version/events until next scheduled health check fires",
                machineId);
        }
    }

    // ── Response shaping ─────────────────────────────────────────────────────

    private static UpgradeMachineResponseData BuildResponse(Persistence.Entities.Deployments.Machine machine, string currentVersion, string targetVersion, MachineUpgradeStatus status, string detail) => new()
    {
        MachineId = machine.Id,
        MachineName = machine.Name,
        CurrentVersion = currentVersion ?? string.Empty,
        TargetVersion = targetVersion ?? string.Empty,
        Status = status,
        Detail = detail
    };

    private static string NoTargetVersionDetail()
        => "Could not resolve target tentacle version: no operator override "
         + $"({TentacleVersionRegistry.LinuxOverrideEnvVar} / {TentacleVersionRegistry.K8sOverrideEnvVar}), "
         + "no cached value, and Docker Hub query failed. "
         + "Specify TargetVersion explicitly in the upgrade request.";
}
