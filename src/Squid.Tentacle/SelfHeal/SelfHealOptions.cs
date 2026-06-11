using System.Globalization;
using Serilog;

namespace Squid.Tentacle.SelfHeal;

/// <summary>
/// Operator-tunable knobs for the disk-pressure self-heal sweep, resolved once
/// from environment variables at process start (mirrors
/// <c>LocalScriptService.OrphanMaxAge</c>). Rule 8: anything an air-gapped /
/// fork operator might need to override lives behind an env var with a pinned
/// <c>public const string</c> name, and the literal defaults are pinned by a
/// unit test so a "harmless" change is a visible decision.
///
/// <para>Tunable via env var: the per-status retention counts (how many recent
/// succeeded / failed workspaces to keep for post-mortem) and the
/// low-disk trigger (also the normal reclaim target — reclaim back to the same
/// boundary that triggered the sweep). The critical thresholds are deliberately
/// immutable internal hysteresis; they are pinned by test, not exposed.</para>
/// </summary>
public sealed record SelfHealOptions(
    int KeepLatestSucceeded,
    int KeepLatestFailed,
    double LowFreePercentage,
    double CriticalFreePercentage,
    double CriticalTargetFreePercentage)
{
    // ── Env-var override surface (Rule 8 — pinned by SelfHealOptionsTests) ──
    public const string KeepSucceededEnvVar = "SQUID_TENTACLE_SELFHEAL_KEEP_SUCCEEDED";
    public const string KeepFailedEnvVar = "SQUID_TENTACLE_SELFHEAL_KEEP_FAILED";
    public const string LowFreePercentageEnvVar = "SQUID_TENTACLE_SELFHEAL_LOW_FREE_PCT";

    // ── Literal defaults (pinned by SelfHealOptionsTests) ──
    public const int DefaultKeepLatestSucceeded = 10;
    public const int DefaultKeepLatestFailed = 20;
    public const double DefaultLowFreePercentage = 0.20;
    public const double DefaultCriticalFreePercentage = 0.10;
    public const double DefaultCriticalTargetFreePercentage = 0.30;

    // Bounds: keep-counts 0..10_000 (0 = keep nothing under pressure); free
    // percentages strictly inside (0, 1). Out-of-range / unparseable input
    // falls back to the default with a Serilog warning. Public + pinned by test
    // (mirrors LocalScriptService.Min/MaxOrphanMaxAgeHours).
    public const int MinKeepCount = 0;
    public const int MaxKeepCount = 10_000;

    /// <summary>
    /// Short safety floor: a workspace whose directory was last written within
    /// this window is never evicted — even under critical pressure. It guards
    /// the TOCTOU gap in <c>LocalScriptService.StartScript</c> between
    /// <c>Directory.CreateDirectory</c> and the first state <c>Save</c> (and
    /// before the ticket is registered with the running-script reporter), so a
    /// sweep can never delete a workspace a deployment is initialising into. A
    /// freshly-created dir reclaims ~nothing anyway, so protecting it costs
    /// nothing. Not env-tunable — it is a correctness floor, not a policy knob.
    /// </summary>
    public const int FreshWorkspaceGraceSeconds = 60;

    public static TimeSpan FreshWorkspaceGraceWindow => TimeSpan.FromSeconds(FreshWorkspaceGraceSeconds);

    /// <summary>Read once, cached for process lifetime.</summary>
    public static SelfHealOptions Default { get; } = Resolve();

    public RetentionQuota Quota => new(KeepLatestSucceeded, KeepLatestFailed);

    private static SelfHealOptions Resolve() => new(
        ResolveKeepCount(KeepSucceededEnvVar, DefaultKeepLatestSucceeded),
        ResolveKeepCount(KeepFailedEnvVar, DefaultKeepLatestFailed),
        ResolveFreePercentage(LowFreePercentageEnvVar, DefaultLowFreePercentage),
        DefaultCriticalFreePercentage,
        DefaultCriticalTargetFreePercentage);

    private static int ResolveKeepCount(string envVar, int defaultValue)
        => ParseKeepCount(Environment.GetEnvironmentVariable(envVar), envVar, defaultValue);

    private static double ResolveFreePercentage(string envVar, double defaultValue)
        => ParseFreePercentage(Environment.GetEnvironmentVariable(envVar), envVar, defaultValue);

    // Pure parse + validate (no env read) so every branch is directly unit-testable.
    internal static int ParseKeepCount(string raw, string envVar, int defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < MinKeepCount || value > MaxKeepCount)
        {
            Log.Warning("{EnvVar}='{RawValue}' is not a valid integer in [{Min}..{Max}]; falling back to default {Default}",
                envVar, raw, MinKeepCount, MaxKeepCount, defaultValue);
            return defaultValue;
        }

        Log.Information("Self-heal retention {EnvVar} configured to {Value}", envVar, value);

        return value;
    }

    // Invariant culture so an operator's "0.20" parses identically regardless of the
    // server's locale (a comma-decimal locale would otherwise mis-read it).
    internal static double ParseFreePercentage(string raw, string envVar, double defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return defaultValue;

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0.0 || value >= 1.0)
        {
            Log.Warning("{EnvVar}='{RawValue}' is not a valid fraction in (0, 1); falling back to default {Default}",
                envVar, raw, defaultValue);
            return defaultValue;
        }

        Log.Information("Self-heal {EnvVar} configured to {Value}", envVar, value);

        return value;
    }
}
