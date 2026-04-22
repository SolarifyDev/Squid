using System.Diagnostics;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Jobs;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;
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
    /// <para>Sized to comfortably exceed the typical full Phase A + Phase
    /// B sequence (30-40s observed across all install methods) plus
    /// Halibut reconnect jitter.</para>
    /// </summary>
    internal const int UpgradePollingWindowSeconds = 45;

    /// <summary>
    /// Interval between health checks during the upgrade polling window.
    /// 3s matches the FE integration guide's recommended poll cadence —
    /// UI sees events arrive within one FE poll cycle after they're
    /// emitted on the agent.
    ///
    /// <para>Job count = window / interval = 45 / 3 = 15 Hangfire jobs
    /// per upgrade. Each is a quick Capabilities RPC (~20ms on local,
    /// ~200ms over WAN). Upper-bound ~3s of CPU time across all 15
    /// jobs, negligible.</para>
    /// </summary>
    internal const int UpgradePollingIntervalSeconds = 3;

    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IMachineRuntimeCapabilitiesCache _runtimeCache;
    private readonly ITentacleVersionRegistry _versionRegistry;
    private readonly IEnumerable<IMachineUpgradeStrategy> _strategies;
    private readonly IRedisSafeRunner _redisLock;
    private readonly ISquidBackgroundJobClient _backgroundJobClient;

    public MachineUpgradeService(IMachineDataProvider machineDataProvider, IMachineRuntimeCapabilitiesCache runtimeCache, ITentacleVersionRegistry versionRegistry, IEnumerable<IMachineUpgradeStrategy> strategies, IRedisSafeRunner redisLock, ISquidBackgroundJobClient backgroundJobClient)
    {
        _machineDataProvider = machineDataProvider;
        _runtimeCache = runtimeCache;
        _versionRegistry = versionRegistry;
        _strategies = strategies;
        _redisLock = redisLock;
        _backgroundJobClient = backgroundJobClient;
    }

    public async Task<UpgradeMachineResponseData> UpgradeAsync(UpgradeMachineCommand command, CancellationToken ct)
    {
        // Audit trail wrapper (audit A7). Every upgrade attempt — success,
        // rejected, or exception — leaves two Seq log lines with the
        // `[UpgradeAudit]` prefix for ops filtering. Outcome log uses
        // structured props (status, elapsedMs) so it's aggregate-queryable.
        // ICurrentUser integration + log-sink-based contract tests are
        // Phase 2 work; today the logs capture what we know without
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
        var strategy = ResolveStrategy(style);

        if (strategy == null)
            return BuildResponse(machine, currentVersion: null, targetVersion: null, MachineUpgradeStatus.NotSupported, $"No upgrade strategy registered for CommunicationStyle '{style}'.");

        var targetVersion = await ResolveTargetVersionAsync(command, style, ct).ConfigureAwait(false);

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

        var latestVersion = await ResolveLatestForInfoAsync(style, ct).ConfigureAwait(false);

        var (canUpgrade, reason) = EvaluateUpgradeEligibility(style, currentVersion, latestVersion);

        return new GetUpgradeInfoResponseData
        {
            MachineId = machine.Id,
            CurrentVersion = currentVersion ?? string.Empty,
            LatestAvailableVersion = latestVersion ?? string.Empty,
            CanUpgrade = canUpgrade,
            Reason = reason
        };
    }

    /// <summary>
    /// Skip the version-registry call for styles we can't upgrade anyway —
    /// no point burning Docker Hub quota on an SSH target just to render
    /// its info row.
    /// </summary>
    private async Task<string> ResolveLatestForInfoAsync(string style, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(style) || ResolveStrategy(style) == null)
            return string.Empty;

        return await _versionRegistry.GetLatestVersionAsync(style, ct).ConfigureAwait(false) ?? string.Empty;
    }

    /// <summary>
    /// Pure decision: given what we know, is it worth showing the operator
    /// an upgrade affordance? Each branch returns a reason the FE can
    /// render verbatim in a tooltip.
    /// </summary>
    private (bool canUpgrade, string reason) EvaluateUpgradeEligibility(string style, string currentVersion, string latestVersion)
    {
        if (string.IsNullOrWhiteSpace(style))
            return (false, "Machine endpoint JSON is malformed or missing CommunicationStyle — re-run machine registration.");

        if (ResolveStrategy(style) == null)
            return (false, $"CommunicationStyle '{style}' is not supported for in-UI upgrades — use the style's manual upgrade path.");

        if (string.IsNullOrEmpty(latestVersion))
            return (false, "Could not resolve the latest available version (Docker Hub unreachable and no env override). Set SQUID_TARGET_LINUX_TENTACLE_VERSION on the server pod to pin a version.");

        if (string.IsNullOrWhiteSpace(currentVersion))
            return (true, "Current agent version is unknown (machine has not been health-checked yet). Upgrade will dispatch; AlreadyUpToDate will catch it if the agent is already on the target.");

        if (!SemVer.TryParse(currentVersion, out var parsedCurrent) || !SemVer.TryParse(latestVersion, out var parsedLatest))
            return (true, $"Cannot compare versions strictly (non-semver current='{currentVersion}'). Upgrade is allowed; worst case the agent's per-version idempotency lock no-ops the run.");

        var compare = parsedCurrent.CompareTo(parsedLatest);

        if (compare < 0) return (true, $"Latest published version {latestVersion} is newer than current {currentVersion}.");
        if (compare == 0) return (false, $"Already on version {latestVersion}; nothing to do.");

        return (false, $"Current version {currentVersion} is newer than the latest published ({latestVersion}) — likely a pre-release or dev build. Use AllowDowngrade=true on the upgrade endpoint if a downgrade is deliberate.");
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

    private async Task<string> ResolveTargetVersionAsync(UpgradeMachineCommand command, string style, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(command.TargetVersion)) return command.TargetVersion.Trim();

        return await _versionRegistry.GetLatestVersionAsync(style, ct).ConfigureAwait(false);
    }

    private IMachineUpgradeStrategy ResolveStrategy(string style)
    {
        // Single-owner invariant. Without this, a future refactor widening
        // two strategies' CanHandle() to overlap would silently dispatch
        // to whichever Autofac registered first — the operator sees no
        // warning, gets the wrong transport (or wrong URL pattern, or
        // wrong script), and debugging is brutal. Throw loudly at the
        // first trigger before any side effect. Surfaces both class names
        // so the fix is obvious: narrow CanHandle in one of them.
        var matches = _strategies.Where(s => s.CanHandle(style)).ToList();

        if (matches.Count > 1)
            throw new InvalidOperationException(
                $"Multiple upgrade strategies claim CommunicationStyle '{style}': " +
                $"{string.Join(", ", matches.Select(m => m.GetType().Name))}. " +
                "Each style must have exactly one owner. Narrow CanHandle() in one of them.");

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

        var result = await _redisLock.ExecuteWithLockAsync<UpgradeMachineResponseData>(lockKey, () => RunStrategyAsync(machine, strategy, targetVersion, currentVersion, ct), expiry: LockExpiry, wait: LockWait, retry: LockRetry).ConfigureAwait(false);

        return result ?? BuildResponse(machine, currentVersion, targetVersion, MachineUpgradeStatus.Failed,
            $"Machine '{machine.Name}' is currently being upgraded by another request. " +
            "Wait for it to complete (typically under 2 minutes) and retry.");
    }

    private async Task<UpgradeMachineResponseData> RunStrategyAsync(Persistence.Entities.Deployments.Machine machine, IMachineUpgradeStrategy strategy, string targetVersion, string currentVersion, CancellationToken ct)
    {
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

        // Rapid-polling during the upgrade window: schedule N staggered
        // Hangfire jobs that each re-capture the agent's upgrade-events
        // JSONL + Phase B log via Capabilities RPC. Populates the
        // server-side timeline store so the FE's 2-3s polling of
        // /upgrade-events sees near-real-time progress instead of
        // "empty until 45s post-upgrade."
        //
        // Fire-and-forget: errors here must not bubble back to the
        // operator's HTTP request (the upgrade already succeeded; a
        // failed observability-poll is a cosmetic issue). Try/catch
        // guards against Hangfire storage exceptions (Redis down etc).
        //
        // Early-completion: if the upgrade finishes in 10s, the remaining
        // jobs still fire — each one is a cheap no-op Capabilities RPC
        // that re-reads the same terminal state (SUCCESS). ~200ms × 15
        // jobs = 3s of total wasted work. Acceptable.
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
