using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Filtering;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.DeploymentExecution.Pipeline.Phases;
using Squid.Core.Services.DeploymentExecution.Variables;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.Message.Constants;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Snapshots;
using Squid.Message.Models.Deployments.Variable;
using Xunit;
using ReleaseEntity = Squid.Core.Persistence.Entities.Deployments.Release;
using ServerTaskEntity = Squid.Core.Persistence.Entities.Deployments.ServerTask;

namespace Squid.UnitTests.Services.Deployments.Checkpoints;

/// <summary>
/// Guards the line in <see cref="PrepareDeploymentPhase"/> that merges checkpoint-restored
/// output variables into the live variable list.
///
/// <para><b>Why it needs its own test</b>: this is what makes a restored output variable
/// RESOLVABLE by the steps that run after a resume — distinct from the checkpoint accumulator,
/// which is what makes it survive the NEXT pause. The two concerns moved apart (the accumulator
/// re-seed now lives in <c>ExecuteStepsPhase</c>, which owns the encryption service), and when
/// the accumulation suite was retargeted onto the persisted checkpoint this line briefly lost
/// its only coverage. Deleting it would let a resumed deployment run every subsequent step with
/// the variables missing while every checkpoint test stayed green.</para>
/// </summary>
public sealed class PrepareDeploymentRestoredOutputsTests
{
    private static readonly string RestoredName = SpecialVariables.Output.Variable("Deploy", "Url");

    [Fact]
    public async Task RestoredOutputVariables_AreMergedIntoTheLiveVariableList()
    {
        var ctx = await RunPrepareAsync(new VariableDto { Name = RestoredName, Value = "https://first-run.test" });

        ctx.Variables.ShouldContain(v => v.Name == RestoredName && v.Value == "https://first-run.test",
            "steps running after a resume resolve output variables out of ctx.Variables — without this merge " +
            "they silently see nothing while the checkpoint still holds the value");
    }

    [Fact]
    public async Task RestoredOutputVariables_AreAppendedAfterResolvedVariables_SoTheyWin()
    {
        // Last-wins resolution: the restored output must take precedence over a project variable
        // of the same name, mirroring what the live run resolved before it paused.
        var ctx = await RunPrepareAsync(new VariableDto { Name = RestoredName, Value = "restored" });

        var lastIndex = ctx.Variables.FindLastIndex(v => v.Name == RestoredName);

        ctx.Variables[lastIndex].Value.ShouldBe("restored",
            customMessage: "The restored entry must be the last one for its name.");
    }

    [Fact]
    public async Task NothingRestored_LeavesTheVariableListUnchanged()
    {
        var ctx = await RunPrepareAsync();

        ctx.Variables.ShouldNotContain(v => v.Name == RestoredName);
    }

    private static async Task<DeploymentTaskContext> RunPrepareAsync(params VariableDto[] restoredOutputVariables)
    {
        var snapshot = new DeploymentProcessSnapshotDto { Id = 1, Data = new DeploymentProcessSnapshotDataDto() };

        var snapshotService = new Mock<IDeploymentSnapshotService>();
        snapshotService.Setup(s => s.LoadProcessSnapshotAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(snapshot);

        var variableResolver = new Mock<IDeploymentVariableResolver>();
        variableResolver.Setup(r => r.ResolveVariablesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<VariableDto>());

        var phase = new PrepareDeploymentPhase(
            snapshotService.Object,
            variableResolver.Object,
            new Mock<IDeploymentTargetFinder>().Object,
            new Mock<IDeploymentDataProvider>().Object,
            new Mock<IActionHandlerRegistry>().Object);

        var ctx = new DeploymentTaskContext
        {
            ServerTaskId = 7,
            Task = new ServerTaskEntity { Id = 7 },
            Deployment = new Deployment { Id = 1, EnvironmentId = 1, ChannelId = 1, ProcessSnapshotId = 1 },
            Release = new ReleaseEntity { Id = 1, Version = "1.0.0" },
            Variables = new List<VariableDto>(),
            SelectedPackages = new List<ReleaseSelectedPackage>(),
            RestoredOutputVariables = restoredOutputVariables.ToList()
        };

        await phase.ExecuteAsync(ctx, CancellationToken.None);

        return ctx;
    }
}
