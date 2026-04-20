using System.Diagnostics;
using Squid.Core.Services.Caching.Redis;
using Squid.Core.Services.DeploymentExecution.Tentacle;
using Squid.Core.Services.Machines.Exceptions;
using Squid.Message.Commands.Machine;
using Squid.Message.Requests.Machines;

namespace Squid.Core.Services.Machines.Upgrade;

public sealed class MachineUpgradeService : IMachineUpgradeService
{
    /// <summary>
    /// Outer cap for the lock window. Generous so the underlying upgrade has
    /// room to drain in-flight scripts (waited on by the agent's
    /// FullIsolation mutex), download the tarball, swap, restart, and verify.
    ///
    /// <para><b>Invariant (audit H-15):</b> MUST be strictly greater than the
    /// slowest strategy's wall-clock timeout (currently
    /// <c>LinuxTentacleUpgradeStrategy.UpgradeScriptTimeout = 5min</c>), with
    /// a safety buffer for the lock-acquisition + strategy-teardown window.
    /// Otherwise a hung strategy could outlast the lock and a second replica
    /// would re-dispatch on top. Pinned by
    /// <c>LockExpiry_StrictlyGreaterThanStrategyTimeout</c>.</para>
    /// </summary>
    internal static readonly TimeSpan LockExpiry = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(500);

    private readonly IMachineDataProvider _machineDataProvider;
    private readonly IMachineRuntimeCapabilitiesCache _runtimeCache;
    private readonly ITentacleVersionRegistry _versionRegistry;
    private readonly IEnumerable<IMachineUpgradeStrategy> _strategies;
    private readonly IRedisSafeRunner _redisLock;

    public MachineUpgradeService(IMachineDataProvider machineDataProvider, IMachineRuntimeCapabilitiesCache runtimeCache, ITentacleVersionRegistry versionRegistry, IEnumerable<IMachineUpgradeStrategy> strategies, IRedisSafeRunner redisLock)
    {
        _machineDataProvider = machineDataProvider;
        _runtimeCache = runtimeCache;
        _versionRegistry = versionRegistry;
        _strategies = strategies;
        _redisLock = redisLock;
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
        var lockKey = $"squid:upgrade:machine:{machine.Id}";

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
    /// have changed — the next health check then repopulates from a fresh
    /// Capabilities probe. Without this, the cached old version would
    /// shadow the upgrade for up to a full health-check interval, making
    /// the UI show "still on N-1" even after the agent reports N.
    ///
    /// <para>Outcome-driven (audit N-6): each strategy explicitly sets
    /// <see cref="MachineUpgradeOutcome.AgentVersionMayHaveChanged"/> based on
    /// what its dispatch path actually does. A new
    /// <see cref="MachineUpgradeStatus"/> value can't accidentally miss
    /// invalidation because the orchestrator never inspects the enum here.</para>
    /// </summary>
    private void InvalidateCacheIfChanged(int machineId, MachineUpgradeOutcome outcome)
    {
        if (outcome.AgentVersionMayHaveChanged) _runtimeCache.Invalidate(machineId);
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
