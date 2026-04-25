using System.Collections.Generic;
using Serilog;
using Serilog.Events;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;
using Squid.Message.Hardening;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// P1-B.7 (Phase-5 follow-up to 2026-04-24 audit) — pins the contract of
/// <see cref="SensitiveValueLeakGuard"/>: the agent emits
/// <c>##squid[setVariable name='X' value='Y' sensitive='False']</c> and the
/// pre-fix server trusted the flag blindly. A compromised or buggy script
/// could therefore mark a secret as non-sensitive and leak it to subsequent
/// step logs / output-variable consumers.
///
/// <para>The guard cross-references the candidate output value against the
/// known-sensitive deployment variable values (those marked
/// <c>IsSensitive=true</c> upstream). On match, the three-mode framework
/// decides:</para>
/// <list type="bullet">
///   <item><b>Off</b> — silent passthrough, no override (legacy behaviour).</item>
///   <item><b>Warn</b> (default) — passthrough + structured Serilog warning naming
///         the variable and the env var to switch to strict. Backward-compat
///         preserved; warning surfaces the leak in operator logs.</item>
///   <item><b>Strict</b> — force <c>IsSensitive=true</c> for the output variable.
///         Operator opts in for production hardening.</item>
/// </list>
///
/// <para>Length floor (<c>MinSensitiveValueLength=4</c>) prevents false positives
/// from short generic values like "1", "y", "200". Aligned with
/// <c>SensitiveValueMasker.MinValueLength=4</c>.</para>
/// </summary>
public sealed class SensitiveValueLeakGuardTests
{
    // ── Constant-name pin (Rule 8) ────────────────────────────────────────────

    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        // Renaming this constant breaks operators who set the env var in their
        // deployment manifest. Hard-pin in test.
        SensitiveValueLeakGuard.EnforcementEnvVar.ShouldBe("SQUID_OUTPUT_VAR_SENSITIVE_LEAK_ENFORCEMENT");
    }

    // ── Agent already reported sensitive=true → no override needed ───────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ShouldForceSensitive_AgentSaidSensitive_AlwaysReturnsTrue(EnforcementMode mode)
    {
        // Caller already keeps the agent's True; guard must propagate, not downgrade.
        var result = SensitiveValueLeakGuard.ShouldForceSensitive(
            outputVariableName: "Token",
            outputVariableValue: "anything-here",
            agentReportedSensitive: true,
            knownSensitiveValues: new HashSet<string>(),
            mode: mode);

        result.ShouldBeTrue("agent already marked sensitive → guard must propagate, never downgrade.");
    }

    // ── No leak (no match against known sensitive values) ─────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ShouldForceSensitive_ValueDoesNotMatchKnownSensitive_ReturnsFalse(EnforcementMode mode)
    {
        var result = SensitiveValueLeakGuard.ShouldForceSensitive(
            outputVariableName: "DeployStatus",
            outputVariableValue: "succeeded",
            agentReportedSensitive: false,
            knownSensitiveValues: new HashSet<string> { "super-secret-token-xyz" },
            mode: mode);

        result.ShouldBeFalse("value doesn't match any known sensitive — no override regardless of mode.");
    }

    [Theory]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ShouldForceSensitive_EmptyValue_ReturnsFalse(EnforcementMode mode)
    {
        var result = SensitiveValueLeakGuard.ShouldForceSensitive(
            outputVariableName: "Maybe",
            outputVariableValue: "",
            agentReportedSensitive: false,
            knownSensitiveValues: new HashSet<string> { "any-secret" },
            mode: mode);

        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ShouldForceSensitive_NullSensitiveSet_ReturnsFalse(EnforcementMode mode)
    {
        // Defensive: caller passes null when there are no upstream sensitive
        // variables. Must not throw, just no-op.
        var result = SensitiveValueLeakGuard.ShouldForceSensitive(
            outputVariableName: "X",
            outputVariableValue: "anything",
            agentReportedSensitive: false,
            knownSensitiveValues: null!,
            mode: mode);

        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void ShouldForceSensitive_ShortValueBelowMinLength_ReturnsFalse(EnforcementMode mode)
    {
        // 3-char value: under MinSensitiveValueLength=4. Generic short tokens
        // ("y", "no", "200") accidentally matching a sensitive var would
        // over-redact harmless output. Aligned with SensitiveValueMasker.
        var result = SensitiveValueLeakGuard.ShouldForceSensitive(
            outputVariableName: "Status",
            outputVariableValue: "yes",
            agentReportedSensitive: false,
            knownSensitiveValues: new HashSet<string> { "yes" },
            mode: mode);

        result.ShouldBeFalse("short values (< 4 chars) skip the leak check to avoid masking generic strings.");
    }

    // ── The actual leak path: value matches a known sensitive variable ───────

    [Fact]
    public void ShouldForceSensitive_OffMode_ValueMatchesSensitive_ReturnsFalse_NoLogEmitted()
    {
        // Off = legacy behaviour. Even when the leak is obvious: no override,
        // no warning. For dev / test / explicit operator opt-out.
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var result = SensitiveValueLeakGuard.ShouldForceSensitive(
                outputVariableName: "ApiKey",
                outputVariableValue: "super-secret-token-xyz",
                agentReportedSensitive: false,
                knownSensitiveValues: new HashSet<string> { "super-secret-token-xyz" },
                mode: EnforcementMode.Off);

            result.ShouldBeFalse("Off mode passthrough — no override.");
            sink.Events.ShouldBeEmpty(
                "Off mode is a silent allow — no warning even on obvious leak.");
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void ShouldForceSensitive_WarnMode_ValueMatchesSensitive_ReturnsFalse_LogsActionableWarning()
    {
        // Warn (default) preserves backward compat — flag NOT overridden — but
        // logs a structured warning naming the env var so the operator can
        // remediate or flip to strict. Variable NAME appears in the log;
        // the VALUE must NOT (that would defeat the whole point).
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var result = SensitiveValueLeakGuard.ShouldForceSensitive(
                outputVariableName: "ApiKey",
                outputVariableValue: "super-secret-token-xyz",
                agentReportedSensitive: false,
                knownSensitiveValues: new HashSet<string> { "super-secret-token-xyz" },
                mode: EnforcementMode.Warn);

            result.ShouldBeFalse("Warn mode preserves agent's flag — backward compat.");

            sink.Events.Count.ShouldBe(1, "Warn mode emits exactly one warning per leak detection.");
            var ev = sink.Events[0];
            ev.Level.ShouldBe(LogEventLevel.Warning);
            var rendered = ev.RenderMessage();

            rendered.ShouldContain("ApiKey", customMessage: "warning must name the leaking variable.");
            rendered.ShouldContain(SensitiveValueLeakGuard.EnforcementEnvVar,
                customMessage: "warning must name the env var so operator knows how to switch to strict.");
            rendered.ShouldNotContain("super-secret-token-xyz",
                customMessage: "warning MUST NOT contain the actual sensitive value — that defeats the whole point.");
        }
        finally
        {
            restore();
        }
    }

    [Fact]
    public void ShouldForceSensitive_StrictMode_ValueMatchesSensitive_ReturnsTrue_LogsOverride()
    {
        // Strict: force IsSensitive=true. Operator has opted in for production
        // hardening. The override IS the active protection.
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var result = SensitiveValueLeakGuard.ShouldForceSensitive(
                outputVariableName: "ApiKey",
                outputVariableValue: "super-secret-token-xyz",
                agentReportedSensitive: false,
                knownSensitiveValues: new HashSet<string> { "super-secret-token-xyz" },
                mode: EnforcementMode.Strict);

            result.ShouldBeTrue("Strict mode forces sensitive=true — the override IS the protection.");

            sink.Events.Count.ShouldBe(1);
            var rendered = sink.Events[0].RenderMessage();
            rendered.ShouldContain("ApiKey");
            rendered.ShouldNotContain("super-secret-token-xyz",
                customMessage: "even in strict mode, log MUST NOT echo the sensitive value.");
        }
        finally
        {
            restore();
        }
    }

    // ── Boundary: exactly MinSensitiveValueLength chars — admitted ───────────

    [Fact]
    public void ShouldForceSensitive_StrictMode_ExactMinLengthMatch_StillForcesSensitive()
    {
        // 4-char value = exactly at the threshold → guard activates.
        var result = SensitiveValueLeakGuard.ShouldForceSensitive(
            outputVariableName: "Token",
            outputVariableValue: "abcd",
            agentReportedSensitive: false,
            knownSensitiveValues: new HashSet<string> { "abcd" },
            mode: EnforcementMode.Strict);

        result.ShouldBeTrue("4-char value is at the threshold → guard activates.");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static (CapturingLogSink Sink, Action Restore) InstallCapturingLogger()
    {
        var original = Log.Logger;
        var sink = new CapturingLogSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (sink, () => Log.Logger = original);
    }

    /// <summary>Mirrors the in-test sink used in LinuxTentacleUpgradeStrategyTests
    /// — kept local rather than promoted to a shared helper because Log.Logger
    /// is process-global; tests using it must restore in finally.</summary>
    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
