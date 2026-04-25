using Serilog;
using Squid.Message.Hardening;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Variables;

/// <summary>
/// Phase-6.5: detects output-variable name collisions during the per-batch
/// merge into <c>_ctx.Variables</c>. Closes the multi-target merge race
/// (Agent B P1.3 / A.5 + A.6 root cause): pre-fix the pipeline did
/// <c>_ctx.Variables.AddRange(result.OutputVariables)</c> blindly — every
/// target's clone of an unqualified output variable was appended, leaving
/// downstream <c>FirstOrDefault(v =&gt; v.Name == "X")</c> reads to silently
/// pick whichever entry happened to land first. A compromised script could
/// emit an unqualified clone of a sensitive deployment variable
/// (<c>Squid.Account.Token</c> et al.) and override its value.
///
/// <para>Three-mode framework — defaults to <see cref="EnforcementMode.Warn"/>
/// to preserve existing behaviour while surfacing the issue in operator logs:</para>
/// <list type="bullet">
///   <item><b>Off</b> — silent passthrough; merged list contains every
///         entry (legacy behaviour). For dev / tests / explicit
///         operator opt-out.</item>
///   <item><b>Warn</b> (default) — passthrough + structured Serilog
///         warning per detected collision, naming the variable AND the
///         env var to switch to strict. Backward-compat preserved.</item>
///   <item><b>Strict</b> — first-writer-wins. The colliding incoming
///         write is dropped; existing value preserved. Operator opts
///         in for production hardening.</item>
/// </list>
///
/// <para><b>Collision criterion</b>: same name, DIFFERENT value. Same-value
/// re-emits (e.g. two targets both reading the same release version) are
/// benign and silently deduplicated regardless of mode.</para>
///
/// <para><b>Sensitive-value redaction</b>: warning templates omit the
/// actual value — only the name is logged. Same rule as
/// <c>SensitiveValueLeakGuard</c>.</para>
///
/// <para>Pinned by <c>OutputVariableMergerTests</c>.</para>
/// </summary>
public static class OutputVariableMerger
{
    /// <summary>
    /// Env var that selects collision-handling mode.
    /// Recognised: <c>off</c> / <c>warn</c> / <c>strict</c>.
    /// Default (unset / blank) is <see cref="EnforcementMode.Warn"/>.
    /// </summary>
    public const string EnforcementEnvVar = "SQUID_OUTPUT_VAR_COLLISION_ENFORCEMENT";

    /// <summary>
    /// Merge <paramref name="incoming"/> into <paramref name="existing"/>
    /// according to <paramref name="mode"/>. Returns the new combined list
    /// AND the names that collided (different value already present).
    /// Caller assigns the returned list back into the deployment context's
    /// <c>Variables</c> property; the collision list is informational
    /// (already logged inside this method).
    /// </summary>
    public static (List<VariableDto> Merged, List<string> Collisions) Merge(
        IReadOnlyList<VariableDto> existing,
        IReadOnlyList<VariableDto> incoming,
        EnforcementMode mode)
    {
        // Index existing by name for O(1) lookup. The first occurrence of
        // each name wins for our purposes — that's the entry the FirstOrDefault
        // consumer would have seen pre-fix.
        var byName = new Dictionary<string, VariableDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in existing)
            byName.TryAdd(v.Name ?? string.Empty, v);

        var merged = new List<VariableDto>(existing);
        var collisions = new List<string>();

        foreach (var v in incoming)
        {
            var name = v.Name ?? string.Empty;

            if (!byName.TryGetValue(name, out var prior))
            {
                merged.Add(v);
                byName[name] = v;
                continue;
            }

            // Same value as already present → benign; skip silently.
            if (string.Equals(prior.Value, v.Value, StringComparison.Ordinal))
                continue;

            // Different value → collision.
            switch (mode)
            {
                case EnforcementMode.Off:
                    // Truly silent: keep both entries AND don't surface the
                    // collision to the caller — operator opted out of the
                    // detection entirely.
                    merged.Add(v);
                    break;

                case EnforcementMode.Warn:
                    collisions.Add(name);
                    merged.Add(v);   // backward compat: keep both entries
                    Log.Warning(
                        "[Deploy] Output variable {VariableName} written by multiple targets with " +
                        "different values. Downstream consumers may pick either value depending on " +
                        "list ordering. Set {EnvVar}=strict to enforce first-writer-wins, or " +
                        "qualify the name explicitly via Squid.Action.{{Step}}.{{Var}} / " +
                        "Squid.Action.{{Step}}[{{Machine}}].{{Var}} to avoid the collision.",
                        name, EnforcementEnvVar);
                    break;

                case EnforcementMode.Strict:
                    collisions.Add(name);
                    // first-writer-wins: drop the incoming and log
                    Log.Warning(
                        "[Deploy] Output variable {VariableName} dropped: existing value from a " +
                        "prior target is preserved (first-writer-wins under " +
                        "{EnvVar}=strict). Quality your name explicitly to avoid the collision.",
                        name, EnforcementEnvVar);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unrecognised EnforcementMode");
            }
        }

        return (merged, collisions);
    }
}
