using Shouldly;
using Squid.Core.Services.DeploymentExecution.Validation;
using Xunit;

namespace Squid.UnitTests.Services.Deployments.Execution.Validation;

/// <summary>
/// Unit tests for the fluent builder on top of <c>IReadOnlyDictionary&lt;string,
/// IReadOnlySet&lt;string&gt;&gt;</c>. The builder is the handler-author-facing
/// API; small bugs here corrupt every handler's static-requirement declaration.
/// </summary>
public class CapabilityRequirementsTests
{
    [Fact]
    public void Empty_ReturnsEmptyMap()
    {
        CapabilityRequirements.Empty.ShouldBeEmpty();
    }

    [Fact]
    public void Require_OneSlotOneValue_ProducesSingleEntry()
    {
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows);

        reqs.Count.ShouldBe(1);
        reqs[CapabilityKeys.OsSlot].ShouldBe(new[] { CapabilityKeys.Os.Windows });
    }

    [Fact]
    public void Require_OneSlotMultipleValues_ProducesOrWithinSlot()
    {
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows, CapabilityKeys.Os.Linux, CapabilityKeys.Os.MacOS);

        reqs.Count.ShouldBe(1);
        reqs[CapabilityKeys.OsSlot].Count.ShouldBe(3);
        reqs[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Windows);
        reqs[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Linux);
        reqs[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.MacOS);
    }

    [Fact]
    public void Require_MultipleSlots_ProducesAndAcrossSlots()
    {
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.Shell.PowerShell, CapabilityKeys.Present);

        reqs.Count.ShouldBe(2);
        reqs[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Windows);
        reqs[CapabilityKeys.Shell.PowerShell].ShouldContain(CapabilityKeys.Present);
    }

    [Fact]
    public void Require_SameSlotTwice_LastCallWins()
    {
        // Documents that Require is dictionary-set semantics (replace), not merge.
        // If we ever want merge semantics, expose a separate AddTo() method.
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Windows)
            .Require(CapabilityKeys.OsSlot, CapabilityKeys.Os.Linux, CapabilityKeys.Os.MacOS);

        reqs[CapabilityKeys.OsSlot].Count.ShouldBe(2);
        reqs[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.Linux);
        reqs[CapabilityKeys.OsSlot].ShouldContain(CapabilityKeys.Os.MacOS);
        reqs[CapabilityKeys.OsSlot].ShouldNotContain(CapabilityKeys.Os.Windows,
            customMessage:
                "Second Require on the same slot REPLACES (not merges). " +
                "If you need merge, add an explicit AddTo / Union method — don't change Require's semantics.");
    }

    [Fact]
    public void Require_EmptyValueList_Throws()
    {
        // A slot with zero acceptable values can never be satisfied — the handler
        // is misconfigured. Throw eagerly at construction time, not at planner time.
        Should.Throw<ArgumentException>(() =>
            CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Require_BlankSlot_Throws(string slot)
    {
        Should.Throw<ArgumentException>(() =>
            CapabilityRequirements.Empty.Require(slot, "anything"));
    }

    [Fact]
    public void Require_NullAcceptableValuesArray_Throws()
    {
        Should.Throw<ArgumentNullException>(() =>
            CapabilityRequirements.Empty.Require(CapabilityKeys.OsSlot, null));
    }

    [Fact]
    public void Require_SlotsAreCaseInsensitive()
    {
        var reqs = CapabilityRequirements.Empty
            .Require("OS", CapabilityKeys.Os.Windows);

        reqs.ContainsKey("os").ShouldBeTrue();
        reqs.ContainsKey("Os").ShouldBeTrue();
        reqs.ContainsKey("OS").ShouldBeTrue();
    }

    [Fact]
    public void Require_ValuesAreCaseInsensitive()
    {
        var reqs = CapabilityRequirements.Empty
            .Require(CapabilityKeys.OsSlot, "WINDOWS");

        reqs[CapabilityKeys.OsSlot].Contains("windows").ShouldBeTrue();
        reqs[CapabilityKeys.OsSlot].Contains("Windows").ShouldBeTrue();
    }
}
