using System.Collections.Generic;
using System.Linq;
using Serilog;
using Serilog.Events;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Hardening;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Phase-6.5: pins the contract of <see cref="OutputVariableMerger"/>.
///
/// <para><b>The bug it closes</b>: <c>_ctx.Variables.AddRange(result.OutputVariables)</c>
/// in <c>ExecuteStepsPhase.ApplyBatchResults</c> blindly appends every output
/// variable from every target. For unqualified names (e.g. user script emitted
/// <c>setVariable name="version" value="A"</c> on target-1 and
/// <c>setVariable name="version" value="B"</c> on target-2), the list ends up
/// with both entries — downstream consumers reading
/// <c>Variables.FirstOrDefault(v =&gt; v.Name == "version")</c> get whichever
/// landed first, with no warning that the OTHER target produced a different
/// value. Worst case: a compromised script overrides a sensitive deployment
/// variable like <c>Squid.Account.Token</c> by emitting an unqualified clone
/// (qualified clones are namespaced per-step-per-machine so they never
/// collide; ONLY unqualified is at risk).</para>
///
/// <para><b>Three-mode framework</b> (default = Warn = preserves existing
/// behaviour + adds operator-visible warning):</para>
/// <list type="bullet">
///   <item><b>Off</b> — silent passthrough; merged list contains every entry
///         (legacy behaviour; dev / test / explicit operator opt-out).</item>
///   <item><b>Warn</b> (default) — passthrough + structured Serilog warning
///         per detected collision, naming the variable + the env var to
///         switch to strict. Backward compat: existing deployments behave
///         identically; the only delta is the warning in the log.</item>
///   <item><b>Strict</b> — first-writer-wins. Subsequent writes of the same
///         name with a different value are DROPPED and logged. Operator
///         opts in for production hardening.</item>
/// </list>
///
/// <para>Same-value writes (target-1 and target-2 both emit version=1.2.3)
/// are NOT collisions — only DIFFERENT values trigger the warn / strict path.
/// Sensitive values are NEVER echoed in log payloads (B.7-style redaction).</para>
/// </summary>
public sealed class OutputVariableMergerTests
{
    [Fact]
    public void EnforcementEnvVar_ConstantNamePinned()
    {
        OutputVariableMerger.EnforcementEnvVar.ShouldBe("SQUID_OUTPUT_VAR_COLLISION_ENFORCEMENT");
    }

    // ── No collision: pass through unchanged in every mode ────────────────────

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void Merge_NoCollision_AllExistingAndIncomingPreserved(EnforcementMode mode)
    {
        // Contract: merged = existing ∪ incoming when there are no name
        // conflicts. Caller assigns the returned list back into _ctx.Variables.
        var existing = new List<VariableDto>
        {
            new() { Name = "Squid.Deployment.Id", Value = "Deployments-1" }
        };
        var incoming = new List<VariableDto>
        {
            new() { Name = "Squid.Action.A.Output", Value = "x" },
            new() { Name = "version", Value = "1.2.3" }
        };

        var (merged, collisions) = OutputVariableMerger.Merge(existing, incoming, mode);

        merged.Count.ShouldBe(3, customMessage: "merged returns existing(1) + incoming(2) when no collisions.");
        collisions.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void Merge_SameNameSameValue_NotACollision(EnforcementMode mode)
    {
        // Two targets emitting the same value for the same name is benign —
        // happens with deterministic outputs like `version=1.2.3` from any
        // target reading the same release. Don't flag it; dedup silently.
        var existing = new List<VariableDto> { new() { Name = "version", Value = "1.2.3" } };
        var incoming = new List<VariableDto> { new() { Name = "version", Value = "1.2.3" } };

        var (merged, collisions) = OutputVariableMerger.Merge(existing, incoming, mode);

        merged.Count.ShouldBe(1, customMessage: "duplicate same-value writes deduplicate silently — they're not a collision.");
        collisions.ShouldBeEmpty();
    }

    // ── Collision detection: different values for the same name ───────────────

    [Fact]
    public void Merge_OffMode_CollisionPassesThroughSilently()
    {
        // Off = legacy. List grows; consumers reading FirstOrDefault get the
        // first entry. No log, no detection.
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var existing = new List<VariableDto> { new() { Name = "version", Value = "A" } };
            var incoming = new List<VariableDto> { new() { Name = "version", Value = "B" } };

            var (merged, collisions) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Off);

            merged.Count.ShouldBe(2, customMessage: "Off mode keeps every entry — legacy behaviour preserved.");
            collisions.ShouldBeEmpty(customMessage: "Off mode does NOT report collisions back to caller.");
            sink.Events.ShouldBeEmpty(customMessage: "Off mode is a silent allow.");
        }
        finally { restore(); }
    }

    [Fact]
    public void Merge_WarnMode_CollisionPassesThrough_WarningLoggedWithVarNameAndEnvVar()
    {
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var existing = new List<VariableDto> { new() { Name = "version", Value = "A" } };
            var incoming = new List<VariableDto> { new() { Name = "version", Value = "B" } };

            var (merged, collisions) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Warn);

            merged.Count.ShouldBe(2, customMessage: "Warn preserves backward compat — list grows as before.");
            collisions.Count.ShouldBe(1);
            collisions[0].ShouldBe("version");

            var warnings = sink.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            warnings.Count.ShouldBe(1, customMessage: "exactly one warning per collision.");

            var rendered = warnings[0].RenderMessage();
            rendered.ShouldContain("version", customMessage: "warning must name the colliding variable.");
            rendered.ShouldContain(OutputVariableMerger.EnforcementEnvVar,
                customMessage: "warning must name the env var so operator knows how to switch to strict.");
        }
        finally { restore(); }
    }

    [Fact]
    public void Merge_StrictMode_CollisionDroppedFirstWriterWins_WarningLogged()
    {
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var existing = new List<VariableDto> { new() { Name = "version", Value = "A" } };
            var incoming = new List<VariableDto> { new() { Name = "version", Value = "B" } };

            var (merged, collisions) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Strict);

            merged.Count.ShouldBe(1, customMessage: "Strict mode drops the colliding incoming write — first-writer-wins.");
            merged[0].Value.ShouldBe("A", customMessage: "first writer wins; existing value preserved.");
            collisions.Count.ShouldBe(1);

            // RenderMessage is an extension with a default arg — Shouldly's
            // ShouldContain(Expression) trips CS0854 on it. Materialise
            // rendered strings first.
            var warningTexts = sink.Events
                .Where(e => e.Level == LogEventLevel.Warning)
                .Select(e => e.RenderMessage())
                .ToList();
            warningTexts.ShouldContain(t => t.Contains("version"));
        }
        finally { restore(); }
    }

    [Fact]
    public void Merge_StrictMode_SensitiveValueNeverEchoedInLog()
    {
        // Same redaction rule as B.7: even when reporting a collision, the
        // ACTUAL sensitive value must never appear in the log payload.
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var existing = new List<VariableDto>
            {
                new() { Name = "Token", Value = "secret-key-AAA", IsSensitive = true }
            };
            var incoming = new List<VariableDto>
            {
                new() { Name = "Token", Value = "secret-key-BBB", IsSensitive = true }
            };

            OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Strict);

            foreach (var ev in sink.Events)
            {
                var rendered = ev.RenderMessage();
                rendered.ShouldNotContain("secret-key-AAA");
                rendered.ShouldNotContain("secret-key-BBB");
            }
        }
        finally { restore(); }
    }

    [Fact]
    public void Merge_MixedQualifiedAndUnqualified_OnlyUnqualifiedCollides()
    {
        // Sanity: the qualified namespacing (Squid.Action.{step}.{var} and
        // Squid.Action.{step}[{machine}].{var}) makes those names unique by
        // construction — they are NEVER collisions even in multi-target
        // deploys. Only the unqualified clone is at risk.
        var existing = new List<VariableDto>
        {
            new() { Name = "Squid.Action.Deploy.version", Value = "A" },
            new() { Name = "Squid.Action.Deploy[m1].version", Value = "A" },
            new() { Name = "version", Value = "A" }
        };
        var incoming = new List<VariableDto>
        {
            new() { Name = "Squid.Action.Deploy[m2].version", Value = "B" },   // distinct qualified name
            new() { Name = "version", Value = "B" }                            // collision
        };

        var (_, collisions) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Warn);

        collisions.Count.ShouldBe(1);
        collisions[0].ShouldBe("version");
    }

    [Fact]
    public void Merge_MultipleCollisions_AllReported()
    {
        var (sink, restore) = InstallCapturingLogger();
        try
        {
            var existing = new List<VariableDto>
            {
                new() { Name = "v1", Value = "A" },
                new() { Name = "v2", Value = "A" }
            };
            var incoming = new List<VariableDto>
            {
                new() { Name = "v1", Value = "B" },
                new() { Name = "v2", Value = "B" }
            };

            var (_, collisions) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Warn);

            collisions.Count.ShouldBe(2);
            collisions.ShouldContain("v1");
            collisions.ShouldContain("v2");

            var warnings = sink.Events.Where(e => e.Level == LogEventLevel.Warning).ToList();
            warnings.Count.ShouldBe(2);
        }
        finally { restore(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private sealed class CapturingLogSink : Serilog.Core.ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
