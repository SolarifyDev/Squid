using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Hardening;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Unit pins for <see cref="CapturedOutputVariableSet"/> and the property that makes the
/// deployment checkpoint trustworthy: <b>a resumed run must resolve every output variable to the
/// same value the live run resolved</b>.
///
/// <para>The accumulator stores the entries <see cref="OutputVariableMerger.MergeDetailed"/>
/// reports as appended, verbatim. Two earlier designs tried to re-derive that set and both
/// diverged from the live list — the tests below pin the exact sequences that caught them, so
/// neither can come back:</para>
/// <list type="bullet">
///   <item><c>A,B,A</c> with no prior value — the merge SKIPS the trailing A (same value as the
///   first-indexed one), so live is [A,B]. Appending the raw incoming list gave [A,B,A] and
///   resolved A instead of B.</item>
///   <item><c>A,B,C,B</c> — the merge compares against the FIRST indexed value (A) and never
///   re-indexes, so the trailing B IS appended and live is [A,B,C,B], resolving B.
///   De-duplicating gave [A,B,C] and resolved C.</item>
///   <item><c>U1,U2,U1</c> where a project variable already shadows the name — every emit differs
///   from that prior value, so all three are appended and live resolves U1. De-duplicating gave
///   [U1,U2] and resolved U2.</item>
/// </list>
/// </summary>
public sealed class CapturedOutputVariableSetTests
{
    private const string Name = "Digest";

    private static VariableDto Var(string name, string value, bool sensitive = false)
        => new() { Name = name, Value = value, IsSensitive = sensitive };

    /// <summary>Marks a value so a test can see whether the protector ran.</summary>
    private static VariableDto Protect(VariableDto v)
        => v.IsSensitive ? new VariableDto(v) { Value = "enc:" + v.Value } : v;

    /// <summary>
    /// Runs the REAL merge over <paramref name="emits"/> (optionally against a shadowing prior
    /// variable), accumulating the reported appends the way the executor does, and returns what
    /// the live list and the captured set each resolve for <see cref="Name"/>.
    /// </summary>
    private static (string Live, string Captured) ResolveBothWays(
        IEnumerable<string> emits, string shadowingPriorValue = null, EnforcementMode mode = EnforcementMode.Warn)
    {
        var live = new List<VariableDto>();
        if (shadowingPriorValue != null) live.Add(Var(Name, shadowingPriorValue));

        var captured = new CapturedOutputVariableSet();

        // One merge call per emit mirrors per-target arrival; the merge re-indexes first-wins on
        // every call, so batching does not change the outcome.
        foreach (var value in emits)
        {
            var outcome = OutputVariableMerger.MergeDetailed(live, new List<VariableDto> { Var(Name, value) }, mode);
            live = outcome.Merged;
            captured.Add(outcome.Appended, Protect);
        }

        return (Resolve(live), Resolve(captured.CheckpointReady));
    }

    /// <summary>Last-wins, matching VariableDictionary's dictionary assignment.</summary>
    private static string Resolve(IEnumerable<VariableDto> variables)
    {
        string resolved = null;
        foreach (var v in variables)
            if (string.Equals(v.Name, Name, StringComparison.OrdinalIgnoreCase)) resolved = v.Value;

        return resolved;
    }

    [Theory]
    [InlineData("A", "B", "A")]           // merge skips the trailing repeat -> live [A,B]
    [InlineData("A", "B", "C", "B")]      // merge appends the trailing repeat -> live [A,B,C,B]
    [InlineData("A", "B", "C")]
    [InlineData("A", "A", "A")]
    public void CapturedSet_ResolvesTheSameValueAsTheLiveList(params string[] emits)
    {
        var (liveValue, capturedValue) = ResolveBothWays(emits);

        capturedValue.ShouldBe(liveValue,
            customMessage: $"Emits [{string.Join(",", emits)}]: the checkpoint must resolve what the live run " +
                           $"resolved. Live='{liveValue}' captured='{capturedValue}' means a resumed deployment " +
                           "silently reconfigures itself with a different target's value.");
    }

    [Theory]
    [InlineData("U1", "U2", "U1")]
    [InlineData("U1", "U2", "U3", "U2")]
    public void CapturedSet_ResolvesTheSameValueAsTheLiveList_WhenAPriorVariableShadowsTheName(params string[] emits)
    {
        // A project/library variable already holds this name, so the merge's first-indexed value
        // is that prior one and EVERY emit differs from it — all of them are appended.
        var (liveValue, capturedValue) = ResolveBothWays(emits, shadowingPriorValue: "project-default");

        capturedValue.ShouldBe(liveValue,
            customMessage: $"Shadowed emits [{string.Join(",", emits)}]: live='{liveValue}' captured='{capturedValue}'. " +
                           "Shadowing makes every emit a collision, so the append list keeps repeats the merged " +
                           "list keeps — de-duplicating here drops the value last-wins actually returns.");
    }

    [Fact]
    public void StrictMode_DroppedWrite_NeverReachesTheCheckpoint()
    {
        // First-writer-wins: the merge never appends the colliding write, so it cannot be
        // resurrected by a resume — the mode the operator opted into stays honest.
        var (liveValue, capturedValue) = ResolveBothWays(new[] { "first", "second" }, mode: EnforcementMode.Strict);

        liveValue.ShouldBe("first");
        capturedValue.ShouldBe("first",
            customMessage: "A write Strict mode dropped must not appear in the checkpoint.");
    }

    [Fact]
    public void Add_StoresTheMergeAppendListVerbatim_InOrder()
    {
        var set = new CapturedOutputVariableSet();

        set.Add(new[] { Var("A", "1"), Var("B", "2") }, Protect);
        set.Add(new[] { Var("C", "3") }, Protect);

        set.CheckpointReady.Select(v => v.Name).ShouldBe(new[] { "A", "B", "C" },
            customMessage: "Append order is what last-wins resolution depends on.");
    }

    [Fact]
    public void Add_ProtectsExactlyOncePerEntry()
    {
        var set = new CapturedOutputVariableSet();
        var protectCalls = 0;

        VariableDto CountingProtect(VariableDto v)
        {
            protectCalls++;
            return Protect(v);
        }

        // Ten batch boundaries, each appending one new sensitive variable.
        for (var i = 0; i < 10; i++)
            set.Add(new[] { Var($"S{i}", $"secret{i}", sensitive: true) }, CountingProtect);

        set.Count.ShouldBe(10);
        protectCalls.ShouldBe(10,
            customMessage: $"Ten entries must cost ten protections, got {protectCalls}. More means the checkpoint " +
                           "is re-encrypting entries it already encrypted — a 600k-iteration PBKDF2 derivation each.");
    }

    [Fact]
    public void CheckpointReady_HoldsProtectedClones_LeavingCallerVariablesUntouched()
    {
        var set = new CapturedOutputVariableSet();
        var live = Var("S", "secret", sensitive: true);

        set.Add(new[] { live }, Protect);

        live.Value.ShouldBe("secret",
            customMessage: "The live variable must stay plaintext — downstream substitution depends on it.");
        set.CheckpointReady[0].Value.ShouldBe("enc:secret",
            customMessage: "The stored entry must be the protected clone.");
    }

    [Fact]
    public void Add_NullCollectionOrNullEntries_AreIgnored()
    {
        var set = new CapturedOutputVariableSet();

        set.Add(null, Protect).ShouldBe(0);
        set.Add(new VariableDto[] { null }, Protect).ShouldBe(0);

        set.Count.ShouldBe(0);
    }

    [Fact]
    public void Add_NullProtector_Throws()
    {
        var set = new CapturedOutputVariableSet();

        Should.Throw<ArgumentNullException>(() => set.Add(new[] { Var("A", "1") }, null));
    }
}
