using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Message.Constants;
using Squid.Message.Hardening;
using Squid.Message.Models.Deployments.Variable;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution;

/// <summary>
/// Pins <see cref="OutputVariableMerger.SelectAccepted"/> — the bridge between collision
/// handling and what gets checkpointed.
///
/// <para><b>The divergence it prevents</b>: under <see cref="EnforcementMode.Strict"/> the merge
/// drops a colliding incoming output-variable write (first-writer-wins) so it never becomes
/// live. If the checkpoint recorded the raw incoming list instead of the accepted set, resume
/// would restore the dropped value into <c>ctx.Variables</c> and silently resurrect exactly the
/// write the operator asked Strict mode to reject.</para>
/// </summary>
public sealed class OutputVariableMergerSelectAcceptedTests
{
    private static readonly string Name = SpecialVariables.Output.Variable("Deploy", "Url");

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    public void SelectAccepted_NonStrictCollision_KeepsTheIncomingWrite(EnforcementMode mode)
    {
        // Off/Warn do not drop, so the incoming write is live and must be checkpointed.
        var existing = new List<VariableDto> { new() { Name = Name, Value = "first" } };
        var incoming = new List<VariableDto> { new() { Name = Name, Value = "second" } };

        var (merged, _) = OutputVariableMerger.Merge(existing, incoming, mode);

        var accepted = OutputVariableMerger.SelectAccepted(merged, incoming);

        accepted.ShouldHaveSingleItem();
        accepted.Single().Value.ShouldBe("second");
    }

    [Fact]
    public void SelectAccepted_StrictCollision_ExcludesTheDroppedWrite()
    {
        // Strict drops the incoming write; it must NOT reach the checkpoint, or a later resume
        // would restore it and defeat first-writer-wins.
        var existing = new List<VariableDto> { new() { Name = Name, Value = "first" } };
        var incoming = new List<VariableDto> { new() { Name = Name, Value = "second" } };

        var (merged, collisions) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Strict);

        collisions.ShouldContain(Name, "premise: Strict must report this as a collision");
        merged.Single(v => v.Name == Name).Value.ShouldBe("first", "premise: Strict keeps the first writer");

        var accepted = OutputVariableMerger.SelectAccepted(merged, incoming);

        accepted.ShouldBeEmpty(
            "the dropped write must be excluded from the checkpoint set — persisting it would let " +
            "resume resurrect a value Strict mode deliberately rejected");
    }

    [Fact]
    public void SelectAccepted_StrictPartialCollision_KeepsOnlyTheNonCollidingWrites()
    {
        var collidingName = SpecialVariables.Output.Variable("Deploy", "Url");
        var freshName = SpecialVariables.Output.Variable("Deploy", "BuildId");

        var existing = new List<VariableDto> { new() { Name = collidingName, Value = "first" } };
        var incoming = new List<VariableDto>
        {
            new() { Name = collidingName, Value = "second" },
            new() { Name = freshName, Value = "42" }
        };

        var (merged, _) = OutputVariableMerger.Merge(existing, incoming, EnforcementMode.Strict);

        var accepted = OutputVariableMerger.SelectAccepted(merged, incoming);

        accepted.Select(v => v.Name).ShouldBe([freshName]);
    }

    [Theory]
    [InlineData(EnforcementMode.Off)]
    [InlineData(EnforcementMode.Warn)]
    [InlineData(EnforcementMode.Strict)]
    public void SelectAccepted_NoCollision_KeepsEveryIncomingWrite(EnforcementMode mode)
    {
        // The common case: all three name forms of one capture are accepted in every mode.
        var incoming = new List<VariableDto>
        {
            new() { Name = SpecialVariables.Output.Variable("Deploy", "Url"), Value = "u" },
            new() { Name = SpecialVariables.Output.MachineVariable("Deploy", "web-01", "Url"), Value = "u" },
            new() { Name = "Url", Value = "u" }
        };

        var (merged, _) = OutputVariableMerger.Merge(new List<VariableDto>(), incoming, mode);

        OutputVariableMerger.SelectAccepted(merged, incoming).Count.ShouldBe(3);
    }

    [Fact]
    public void SelectAccepted_NothingIncoming_ReturnsEmpty()
    {
        OutputVariableMerger.SelectAccepted([new VariableDto { Name = "X", Value = "1" }], []).ShouldBeEmpty();
        OutputVariableMerger.SelectAccepted([new VariableDto { Name = "X", Value = "1" }], null).ShouldBeEmpty();
    }

    [Fact]
    public void SelectAccepted_EmptyMerged_ReturnsEmpty()
    {
        OutputVariableMerger.SelectAccepted([], [new VariableDto { Name = "X", Value = "1" }]).ShouldBeEmpty();
        OutputVariableMerger.SelectAccepted(null, [new VariableDto { Name = "X", Value = "1" }]).ShouldBeEmpty();
    }
}
