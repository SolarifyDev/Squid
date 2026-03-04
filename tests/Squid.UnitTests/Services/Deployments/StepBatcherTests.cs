using System.Collections.Generic;
using System.Linq;
using Squid.Core.Services.DeploymentExecution;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.Deployments;

public class StepBatcherTests
{
    private static DeploymentStepDto MakeStep(int order, string startTrigger = null)
    {
        return new DeploymentStepDto
        {
            Id = order,
            StepOrder = order,
            Name = $"Step{order}",
            StartTrigger = startTrigger
        };
    }

    [Fact]
    public void BatchSteps_SingleStep_OneBatch()
    {
        var steps = new List<DeploymentStepDto> { MakeStep(1) };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(1);
        batches[0].Count.ShouldBe(1);
    }

    [Fact]
    public void BatchSteps_AllSequential_EachOwnBatch()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1),
            MakeStep(2, "StartAfterPrevious"),
            MakeStep(3)
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(3);
        batches[0].Count.ShouldBe(1);
        batches[1].Count.ShouldBe(1);
        batches[2].Count.ShouldBe(1);
    }

    [Fact]
    public void BatchSteps_StartWithPrevious_Grouped()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1),
            MakeStep(2, "StartWithPrevious")
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(1);
        batches[0].Count.ShouldBe(2);
    }

    [Fact]
    public void BatchSteps_Mixed_CorrectBatching()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1),
            MakeStep(2, "StartWithPrevious"),
            MakeStep(3),
            MakeStep(4),
            MakeStep(5, "StartWithPrevious")
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(3);
        batches[0].Count.ShouldBe(2); // s1, s2
        batches[1].Count.ShouldBe(1); // s3
        batches[2].Count.ShouldBe(2); // s4, s5
    }

    [Fact]
    public void BatchSteps_EmptyList_EmptyBatches()
    {
        var batches = StepBatcher.BatchSteps(new List<DeploymentStepDto>());

        batches.Count.ShouldBe(0);
    }

    [Fact]
    public void BatchSteps_NullList_EmptyBatches()
    {
        var batches = StepBatcher.BatchSteps(null);

        batches.Count.ShouldBe(0);
    }

    [Fact]
    public void BatchSteps_FirstStepStartWithPrevious_StillNewBatch()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1, "StartWithPrevious")
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(1);
        batches[0].Count.ShouldBe(1);
    }

    [Fact]
    public void BatchSteps_CaseInsensitive()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1),
            MakeStep(2, "startwithprevious")
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(1);
        batches[0].Count.ShouldBe(2);
    }

    [Fact]
    public void BatchSteps_ConsecutiveStartWithPrevious_StaysInSameBatch_AndPreservesOrder()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1),
            MakeStep(2, "StartWithPrevious"),
            MakeStep(3, "StartWithPrevious"),
            MakeStep(4)
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(2);
        batches[0].Select(s => s.Id).ShouldBe(new[] { 1, 2, 3 });
        batches[1].Select(s => s.Id).ShouldBe(new[] { 4 });
    }

    [Fact]
    public void BatchSteps_NullStartTrigger_NewBatch()
    {
        var steps = new List<DeploymentStepDto>
        {
            MakeStep(1),
            MakeStep(2) // null StartTrigger
        };

        var batches = StepBatcher.BatchSteps(steps);

        batches.Count.ShouldBe(2);
    }
}
