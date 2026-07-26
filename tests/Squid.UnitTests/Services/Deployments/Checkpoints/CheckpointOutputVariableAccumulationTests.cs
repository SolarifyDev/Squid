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
/// Pins the RESUME half of the checkpoint output-variable contract: variables restored from a
/// checkpoint must be re-seeded into
/// <see cref="DeploymentTaskContext.CapturedOutputVariables"/>, not merely into
/// <c>ctx.Variables</c>.
///
/// <para><b>Why this is load-bearing</b>: the checkpoint is written from the captured set. A
/// deployment that pauses TWICE (e.g. a transient agent blip, resume, then a second blip)
/// would otherwise checkpoint only the outputs captured since the most recent resume, silently
/// discarding everything the earlier run produced — the same class of silent loss the
/// bracket/dot prefix bug caused, just one resume later. Only a multi-pause test catches it;
/// a single pause→resume passes either way.</para>
/// </summary>
public sealed class CheckpointOutputVariableAccumulationTests
{
    [Fact]
    public async Task Resume_RestoredOutputVariables_AreReSeededIntoTheCapturedSet()
    {
        var restored = new VariableDto { Name = SpecialVariables.Output.Variable("Deploy", "Url"), Value = "https://first-run.test" };

        var ctx = await RunPrepareAsync(restored);

        ctx.CapturedOutputVariables.ShouldContain(v => v.Name == restored.Name,
            "a restored output variable MUST re-enter the captured set, otherwise the NEXT checkpoint " +
            "written by this run silently drops everything the previous run produced");
    }

    [Fact]
    public async Task Resume_RestoredOutputVariables_AreAlsoAvailableToSubsequentSteps()
    {
        // Pre-existing behaviour that must not regress: restored outputs are resolvable by
        // later steps via ctx.Variables.
        var restored = new VariableDto { Name = SpecialVariables.Output.Variable("Deploy", "Url"), Value = "https://first-run.test" };

        var ctx = await RunPrepareAsync(restored);

        ctx.Variables.ShouldContain(v => v.Name == restored.Name && v.Value == "https://first-run.test");
    }

    [Fact]
    public async Task FreshDeployment_NothingRestored_LeavesCapturedSetEmpty()
    {
        var ctx = await RunPrepareAsync();

        ctx.CapturedOutputVariables.ShouldBeEmpty(
            "a fresh deployment has captured nothing yet — seeding anything here would checkpoint phantom values");
    }

    /// <summary>
    /// Drives the real <see cref="PrepareDeploymentPhase"/> (the phase that consumes
    /// <c>RestoredOutputVariables</c>) with an empty process so the target-finding branch is
    /// skipped and the variable-merge path is what is under test.
    /// </summary>
    private static async Task<DeploymentTaskContext> RunPrepareAsync(params VariableDto[] restoredOutputVariables)
    {
        var snapshot = new DeploymentProcessSnapshotDto
        {
            Id = 1,
            Data = new DeploymentProcessSnapshotDataDto()
        };

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
