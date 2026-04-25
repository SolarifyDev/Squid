using Serilog;
using Squid.Message.Hardening;

namespace Squid.Core.Services.DeploymentExecution.Script.ServiceMessages;

/// <summary>
/// Guards against agent-stdout-trusted <c>sensitive='False'</c> overrides
/// leaking known-sensitive values into subsequent step logs / output-variable
/// consumers.
///
/// <para><b>P1-B.7 (Phase-5 follow-up to 2026-04-24 audit)</b>: pre-fix the
/// server trusted the agent's <c>sensitive=</c> attribute on
/// <c>##squid[setVariable]</c> messages blindly. A compromised or buggy
/// script could mark a secret as non-sensitive — the value would then be
/// echoed verbatim into Seq logs, post-step variable readouts, and any
/// downstream step that consumed the captured output variable.</para>
///
/// <para>The guard cross-references each candidate output value against the
/// known-sensitive deployment variable values (those marked
/// <c>IsSensitive=true</c> upstream). On match, the three-mode framework
/// decides:</para>
/// <list type="bullet">
///   <item><b>Off</b> — silent passthrough, no override. Legacy behaviour;
///         dev / test / explicit operator opt-out.</item>
///   <item><b>Warn</b> (default) — passthrough + structured Serilog warning
///         naming the variable and the env var to switch to strict.
///         Backward-compat preserved; warning surfaces the leak in operator
///         logs without changing observable behaviour.</item>
///   <item><b>Strict</b> — force <c>IsSensitive=true</c> for the output
///         variable. Operator opts in for production hardening.</item>
/// </list>
///
/// <para>The 4-character minimum (<see cref="MinSensitiveValueLength"/>)
/// prevents false positives from short generic values like <c>"yes"</c>,
/// <c>"200"</c>, <c>"on"</c>. Aligned with
/// <c>SensitiveValueMasker.MinValueLength = 4</c>.</para>
///
/// <para>Pinned by <c>SensitiveValueLeakGuardTests</c>.</para>
/// </summary>
public static class SensitiveValueLeakGuard
{
    /// <summary>
    /// Env var that selects the leak-guard mode. Recognised:
    /// <c>off</c> / <c>warn</c> / <c>strict</c>.
    ///
    /// <para>Default (unset / blank) is <see cref="EnforcementMode.Warn"/>:
    /// preserves backward compat and surfaces leaks in operator logs.</para>
    ///
    /// <para>Pinned literal; renaming breaks operator-documented path.</para>
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_OUTPUT_VAR_SENSITIVE_LEAK_ENFORCEMENT";

    /// <summary>
    /// Minimum length for a value to be considered against the known-sensitive
    /// set. Below this floor, false-positive risk (generic short tokens
    /// matching a deployment variable by chance) outweighs the leak risk.
    /// Aligned with <c>SensitiveValueMasker.MinValueLength</c>.
    /// </summary>
    public const int MinSensitiveValueLength = 4;

    /// <summary>
    /// Decide whether to force <c>IsSensitive=true</c> for an output variable
    /// the agent reported as <c>sensitive='False'</c>.
    /// Returns <c>true</c> only when the mode requires the override AND the
    /// value matches a known sensitive deployment value.
    /// Caller wires the result via <c>isSensitive = agent || ShouldForceSensitive(...)</c>.
    /// </summary>
    public static bool ShouldForceSensitive(
        string outputVariableName,
        string outputVariableValue,
        bool agentReportedSensitive,
        IReadOnlyCollection<string> knownSensitiveValues,
        EnforcementMode mode)
    {
        // Already sensitive — propagate without re-checking. Cheaper and
        // avoids duplicate logging on the obvious case.
        if (agentReportedSensitive) return true;

        // Off mode: legacy behaviour, never override, never log.
        if (mode == EnforcementMode.Off) return false;

        // No value, no leak possible.
        if (string.IsNullOrEmpty(outputVariableValue)) return false;

        // Defensive: no upstream sensitive variables → nothing to leak against.
        if (knownSensitiveValues == null || knownSensitiveValues.Count == 0) return false;

        // Length floor — generic short values are too likely to false-positive.
        if (outputVariableValue.Length < MinSensitiveValueLength) return false;

        if (!ContainsOrdinal(knownSensitiveValues, outputVariableValue)) return false;

        if (mode == EnforcementMode.Warn)
        {
            // Warn: do NOT change behaviour; log only. The value is OMITTED
            // from the warning template — logging it would defeat the entire
            // point of the guard.
            Log.Warning(
                "Output variable {OutputVariableName} has agent-reported sensitive=False, but its " +
                "value matches a known sensitive deployment variable. The value would be exposed " +
                "in plaintext to subsequent step logs and output-variable consumers. Set " +
                "{EnvVar}=strict to force sensitive=true and protect the value, or remove the " +
                "echo from the script.",
                outputVariableName, EnforcementEnvVar);
            return false;
        }

        // Strict — operator has opted in for production hardening; force the override.
        // Same redaction rule: variable NAME in the log, VALUE never.
        Log.Warning(
            "Output variable {OutputVariableName} forced to sensitive=true: value matched a known " +
            "deployment sensitive value but the agent reported sensitive=False. Likely a script " +
            "bug or a compromised script attempting to leak secrets.",
            outputVariableName);
        return true;
    }

    private static bool ContainsOrdinal(IReadOnlyCollection<string> set, string candidate)
    {
        // HashSet<string> path — O(1) when the caller passes one with ordinal
        // comparer. Fallback to linear scan for arbitrary IReadOnlyCollection
        // (kept for test ergonomics — production callers should pass HashSet).
        if (set is HashSet<string> hs)
            return hs.Comparer.Equals(StringComparer.Ordinal)
                ? hs.Contains(candidate)
                : LinearContainsOrdinal(set, candidate);

        return LinearContainsOrdinal(set, candidate);
    }

    private static bool LinearContainsOrdinal(IReadOnlyCollection<string> set, string candidate)
    {
        foreach (var v in set)
            if (string.Equals(v, candidate, StringComparison.Ordinal))
                return true;
        return false;
    }
}
