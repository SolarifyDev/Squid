using Shouldly;
using Squid.Calamari.Pipeline;
using Xunit;

namespace Squid.Calamari.Tests.Calamari.Pipeline;

/// <summary>
/// PR-5 — record-level tests for <see cref="StepOutcome"/>. Pins the
/// public surface (status values, factory shape, default-empty metrics
/// dict) that future UI / SDK consumers depend on.
/// </summary>
public sealed class StepOutcomeTests
{
    [Fact]
    public void Success_FactoryProducesSucceededStatus()
    {
        var outcome = StepOutcome.Success("MyStep");
        outcome.StepName.ShouldBe("MyStep");
        outcome.Status.ShouldBe(StepStatus.Succeeded);
        outcome.Message.ShouldBeNull();
        outcome.Metrics.ShouldBeEmpty();
        outcome.DurationMs.ShouldBe(0);
    }

    [Fact]
    public void Success_WithMetrics_AttachesMetricsDict()
    {
        var outcome = StepOutcome.Success("MyStep", new Dictionary<string, long>
        {
            ["FilesProcessed"] = 12,
            ["Errors"] = 0
        });

        outcome.Metrics["FilesProcessed"].ShouldBe(12);
        outcome.Metrics["Errors"].ShouldBe(0);
    }

    [Fact]
    public void Skipped_FactoryNamesReason()
    {
        var outcome = StepOutcome.Skipped("MyStep", "Targets glob is empty");

        outcome.Status.ShouldBe(StepStatus.Skipped);
        outcome.Message.ShouldBe("Targets glob is empty");
    }

    [Fact]
    public void Failed_FactoryNamesError()
    {
        var outcome = StepOutcome.Failed("MyStep", "Exception text");

        outcome.Status.ShouldBe(StepStatus.Failed);
        outcome.Message.ShouldBe("Exception text");
    }

    [Fact]
    public void RecordEquality_SameValuesAreEqual()
    {
        // Pinning record-equality semantics — UI test harnesses can compare
        // by value without needing custom equality. Pinned because someone
        // changing this to a class would silently break test assertions.
        var a = StepOutcome.Success("X");
        var b = StepOutcome.Success("X");
        a.ShouldBe(b);
    }

    [Fact]
    public void StepStatus_EnumStableValues()
    {
        // Pinning enum values — these flow through CommandExecutionResult to
        // downstream consumers. Changing them is binary-breaking for any
        // serialised channel.
        ((int)StepStatus.Succeeded).ShouldBe(1);
        ((int)StepStatus.Skipped).ShouldBe(2);
        ((int)StepStatus.Failed).ShouldBe(3);
    }
}
