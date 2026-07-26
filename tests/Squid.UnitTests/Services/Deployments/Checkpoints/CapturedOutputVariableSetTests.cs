using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Unit pins for <see cref="CapturedOutputVariableSet"/> — the accumulator that decides what
/// the deployment checkpoint holds.
///
/// <para>Two properties matter and both are load-bearing for correctness, not tidiness:</para>
/// <list type="number">
///   <item><b>De-duplication</b> on the (name, value, sensitivity) triple. A step across N
///   targets re-emits the same variable N times and <c>SelectAccepted</c> reports all N as
///   accepted, so without this the column grows by one duplicate per target per pause/resume
///   cycle, unbounded. The identity mirrors <c>OutputVariableMerger.Merge</c> exactly (name
///   case-insensitive, value ordinal) so the captured set reproduces the live merged list.</item>
///   <item><b>Protect-once</b>. Entries are stored already-protected so a checkpoint write is
///   O(entries added since the last write). Protecting at serialize time instead re-encrypted
///   the whole accumulated set on every batch boundary — at 600k PBKDF2 iterations per value
///   that is hundreds of milliseconds each, growing quadratically with batch count.</item>
/// </list>
/// </summary>
public sealed class CapturedOutputVariableSetTests
{
    private static VariableDto Var(string name, string value, bool sensitive = false)
        => new() { Name = name, Value = value, IsSensitive = sensitive };

    /// <summary>Marks a value so a test can see whether the protector ran.</summary>
    private static VariableDto Protect(VariableDto v)
        => v.IsSensitive ? new VariableDto(v) { Value = "enc:" + v.Value } : v;

    [Fact]
    public void Add_SameNameAndValueFromSeveralTargets_KeepsOneEntry()
    {
        var set = new CapturedOutputVariableSet();

        // Three targets, one step, identical emit.
        set.Add(new[] { Var("Url", "https://a") }, Protect);
        set.Add(new[] { Var("Url", "https://a") }, Protect);
        set.Add(new[] { Var("Url", "https://a") }, Protect);

        set.Count.ShouldBe(1,
            customMessage: "A same-value re-emit is skipped by Merge and must not accumulate here either — " +
                           "otherwise the checkpoint grows one copy per target per resume cycle.");
    }

    [Fact]
    public void Add_SameNameDifferentValues_KeepsBoth_MirroringMerge()
    {
        var set = new CapturedOutputVariableSet();

        // Under Warn/Off, Merge appends the second value and keeps BOTH entries. The captured
        // set must reproduce that or a resumed run resolves a different value than the live run.
        set.Add(new[] { Var("Digest", "A") }, Protect);
        set.Add(new[] { Var("Digest", "B") }, Protect);

        set.CheckpointReady.Select(v => v.Value).ShouldBe(new[] { "A", "B" },
            customMessage: "Both values, in first-appearance order — this is exactly what Merge leaves live.");
    }

    [Fact]
    public void Add_ValueRepeatedAfterADifferentValue_StillCollapses_MirroringMerge()
    {
        var set = new CapturedOutputVariableSet();

        // Incoming A, B, A. Merge skips the third (same value as the indexed prior) and leaves
        // live = [A, B]. The captured set must match, or resume resolves differently.
        set.Add(new[] { Var("Digest", "A"), Var("Digest", "B"), Var("Digest", "A") }, Protect);

        set.CheckpointReady.Select(v => v.Value).ShouldBe(new[] { "A", "B" },
            customMessage: "A/B/A must collapse to [A, B] — the same list Merge leaves live for that sequence.");
    }

    [Fact]
    public void Add_NamesDifferingOnlyByCase_TreatedAsOne_MatchingMergeNameComparer()
    {
        var set = new CapturedOutputVariableSet();

        // Merge indexes names with OrdinalIgnoreCase, so these are the SAME variable to it.
        set.Add(new[] { Var("Url", "https://a") }, Protect);
        set.Add(new[] { Var("URL", "https://a") }, Protect);

        set.Count.ShouldBe(1,
            customMessage: "Merge indexes names case-insensitively; a case-sensitive identity here would " +
                           "checkpoint a duplicate the live set never had.");
    }

    [Fact]
    public void Add_SameNameAndValueButDifferentSensitivity_KeptSeparately()
    {
        var set = new CapturedOutputVariableSet();

        set.Add(new[] { Var("Token", "abc", sensitive: false) }, Protect);
        set.Add(new[] { Var("Token", "abc", sensitive: true) }, Protect);

        set.Count.ShouldBe(2,
            customMessage: "Sensitivity changes how the value is stored, so collapsing these would silently " +
                           "persist a secret in plaintext under the earlier entry.");
    }

    [Fact]
    public void Add_ProtectsExactlyOncePerKeptEntry()
    {
        var set = new CapturedOutputVariableSet();
        var protectCalls = 0;

        VariableDto CountingProtect(VariableDto v)
        {
            protectCalls++;
            return Protect(v);
        }

        set.Add(new[] { Var("S", "secret", sensitive: true) }, CountingProtect);
        set.Add(new[] { Var("S", "secret", sensitive: true) }, CountingProtect);
        set.Add(new[] { Var("S", "secret", sensitive: true) }, CountingProtect);

        protectCalls.ShouldBe(1,
            customMessage: "The protector must run only for entries actually added. Running it for skipped " +
                           "duplicates re-pays a 600k-iteration PBKDF2 derivation for nothing.");
    }

    [Fact]
    public void RepeatedCheckpointWrites_DoNotRepeatProtection()
    {
        // The regression this guards: cost per checkpoint write must be O(new entries), not
        // O(total entries). Simulates ten batch boundaries over a growing set.
        var set = new CapturedOutputVariableSet();
        var protectCalls = 0;

        VariableDto CountingProtect(VariableDto v)
        {
            protectCalls++;
            return Protect(v);
        }

        for (var batch = 0; batch < 10; batch++)
        {
            // Each batch adds one new sensitive variable and re-emits every earlier one.
            var incoming = new List<VariableDto>();
            for (var i = 0; i <= batch; i++)
                incoming.Add(Var($"S{i}", $"secret{i}", sensitive: true));

            set.Add(incoming, CountingProtect);
        }

        set.Count.ShouldBe(10);
        protectCalls.ShouldBe(10,
            customMessage: $"Ten distinct variables must cost ten protections, not one per (batch x entry). " +
                           $"Got {protectCalls} — quadratic growth means the checkpoint is re-encrypting the " +
                           "whole accumulated set on every batch boundary.");
    }

    [Fact]
    public void CheckpointReady_HoldsProtectedClones_LeavingCallerVariablesUntouched()
    {
        var set = new CapturedOutputVariableSet();
        var live = Var("S", "secret", sensitive: true);

        set.Add(new[] { live }, Protect);

        live.Value.ShouldBe("secret",
            customMessage: "The live variable must stay plaintext — substitution downstream depends on it.");
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
