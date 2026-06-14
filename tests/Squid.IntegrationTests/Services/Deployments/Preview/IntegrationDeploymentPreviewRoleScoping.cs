using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments.Deployments;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.Deployment;

namespace Squid.IntegrationTests.Services.Deployments.Preview;

/// <summary>
/// Integration coverage for the deployment-PREVIEW role-scoping wiring
/// (<c>DeploymentService.GetRequiredRolesAsync</c> feeding the role-scoped
/// <c>TransientDeploymentTargetEvaluator.ApplyProjectPolicy</c> overload). The deployment
/// pipeline half of this behaviour is regression-locked; the preview half shipped untested,
/// the coverage hole the SOTA re-audit flagged.
///
/// <para>The guarded regression: an unavailable target whose role NO enabled step targets must
/// not be evaluated by the project's Transient-Deployment-Targets policy — it is not a target
/// for this release, so it must neither be reported as excluded nor block the deployment.
/// Dropping the role-scoping silently reverts the preview to evaluating EVERY machine in the
/// environment, and the discriminating test below goes red.</para>
///
/// <para>Real Postgres + real EF + the real <see cref="IDeploymentService"/> seam through the
/// real planner/snapshot stack — no mocks on the path under test.</para>
/// </summary>
public class IntegrationDeploymentPreviewRoleScoping : TestBase
{
    public IntegrationDeploymentPreviewRoleScoping()
        : base("DeploymentPreviewRoleScoping", "squid_it_preview_role_scoping")
    {
    }

    [Fact]
    public async Task Preview_StepTargetsOneRole_UnavailableTargetOfUnusedRole_IsNotEvaluatedByPolicy()
    {
        var seed = await SeedAsync(stepTargetRoles: "web").ConfigureAwait(false);

        var result = await PreviewAsync(seed).ConfigureAwait(false);

        var excludedIds = result.ExcludedTargets.Select(target => target.MachineId).ToList();

        excludedIds.ShouldContain(seed.WebMachineId,
            customMessage: "the role-matched unavailable target (role 'web', which the step targets) must be flagged by the default 'Fail deployment' policy");

        excludedIds.ShouldNotContain(seed.DbMachineId,
            customMessage: "an unavailable target whose role ('db') no enabled step targets must NOT be evaluated by the preview policy — preview must scope to the deployment's real step roles, not the whole environment");

        result.BlockingReasons.Any(reason => reason.Contains(seed.DbMachineName)).ShouldBeFalse(
            customMessage: "no blocking reason may name a target the deployment would never touch");
    }

    [Fact]
    public async Task Preview_StepTargetsAllMachines_EveryUnavailableTargetIsEvaluated()
    {
        // Control: a step with NO target roles means "all machines" (CollectAllTargetRoles -> empty
        // -> FilterByRoles applies no narrowing). Both unavailable targets are now real targets, so
        // both must be evaluated by the policy. This proves the discriminator above genuinely flips
        // on step roles rather than passing because the 'db' target was never loaded.
        var seed = await SeedAsync(stepTargetRoles: null).ConfigureAwait(false);

        var result = await PreviewAsync(seed).ConfigureAwait(false);

        var excludedIds = result.ExcludedTargets.Select(target => target.MachineId).ToList();

        excludedIds.ShouldContain(seed.WebMachineId);

        excludedIds.ShouldContain(seed.DbMachineId,
            customMessage: "with a step that targets all machines, the 'db' target is in scope and its unavailability must be evaluated");
    }

    private Task<DeploymentPreviewResult> PreviewAsync(SeedResult seed) =>
        Run<IDeploymentService, DeploymentPreviewResult>(service =>
            service.PreviewDeploymentAsync(new DeploymentRequestPayload { ReleaseId = seed.ReleaseId, EnvironmentId = seed.EnvironmentId }));

    private async Task<SeedResult> SeedAsync(string stepTargetRoles)
    {
        var seed = new SeedResult();

        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy web").ConfigureAwait(false);
            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Run Script", SpecialVariables.ActionTypes.Script).ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id, (SpecialVariables.Action.ScriptBody, "echo hi")).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(stepTargetRoles))
                await builder.CreateStepPropertiesAsync(step.Id, (SpecialVariables.Step.TargetRoles, stepTargetRoles)).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id).ConfigureAwait(false);
            var environment = await builder.CreateEnvironmentAsync().ConfigureAwait(false);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id).ConfigureAwait(false);

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id).ConfigureAwait(false);

            release.ProjectDeploymentProcessSnapshotId = snapshot.Id;
            await repository.UpdateAsync(release).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var web = await builder.CreateMachineAsync(environment.Id, "web-1", "web").ConfigureAwait(false);
            var db = await builder.CreateMachineAsync(environment.Id, "db-1", "db").ConfigureAwait(false);
            await MarkUnavailableAsync(repository, unitOfWork, web, db).ConfigureAwait(false);

            seed.ReleaseId = release.Id;
            seed.EnvironmentId = environment.Id;
            seed.WebMachineId = web.Id;
            seed.DbMachineId = db.Id;
            seed.DbMachineName = db.Name;
        }).ConfigureAwait(false);

        return seed;
    }

    private static async Task MarkUnavailableAsync(IRepository repository, IUnitOfWork unitOfWork, params Machine[] machines)
    {
        foreach (var machine in machines)
            machine.HealthStatus = MachineHealthStatus.Unavailable;

        await repository.UpdateAllAsync(machines).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    private sealed class SeedResult
    {
        public int ReleaseId { get; set; }
        public int EnvironmentId { get; set; }
        public int WebMachineId { get; set; }
        public int DbMachineId { get; set; }
        public string DbMachineName { get; set; }
    }
}
