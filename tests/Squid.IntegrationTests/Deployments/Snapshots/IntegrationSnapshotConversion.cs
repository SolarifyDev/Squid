using Squid.Core.Persistence.Db;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.IntegrationTests.Helpers;

namespace Squid.IntegrationTests.Deployments.Snapshots;

public class IntegrationSnapshotConversion : SnapshotFixtureBase
{
    [Fact]
    public async Task ConvertProcessSnapshotToSteps_FullRoundTrip_ProducesCorrectStepDtos()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();

            // Step 1 with 1 action
            var step1 = await builder.CreateDeploymentStepAsync(process.Id, 1, "Build Step", "Action", "Success");
            await builder.CreateStepPropertiesAsync(step1.Id, (DeploymentVariables.Action.TargetRoles, "build-server"));
            var action1 = await builder.CreateDeploymentActionAsync(step1.Id, 1, "Run Build", "Octopus.Script", isDisabled: false, isRequired: true);
            await builder.CreateActionPropertiesAsync(action1.Id, ("Octopus.Action.Script.ScriptBody", "dotnet build"));

            // Step 2 with 2 actions (one disabled)
            var step2 = await builder.CreateDeploymentStepAsync(process.Id, 2, "Deploy Step", "Action", "Variable");
            await builder.CreateStepPropertiesAsync(step2.Id, (DeploymentVariables.Action.TargetRoles, "web-server"));
            var action2a = await builder.CreateDeploymentActionAsync(step2.Id, 1, "Deploy App", "Octopus.KubernetesDeployContainers", isDisabled: false, isRequired: true);
            await builder.CreateActionPropertiesAsync(action2a.Id, ("Octopus.Action.KubernetesContainers.Namespace", "production"));
            var action2b = await builder.CreateDeploymentActionAsync(step2.Id, 2, "Notify Slack", "Octopus.Script", isDisabled: true, isRequired: false);
            await builder.CreateActionPropertiesAsync(action2b.Id, ("Octopus.Action.Script.ScriptBody", "echo done"));

            // Snapshot → Load → Convert
            var created = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
            var loaded = await snapshotService.LoadProcessSnapshotAsync(created.Id);
            var steps = ProcessSnapshotStepConverter.Convert(loaded);

            // Verify step count
            steps.Count.ShouldBe(2);

            // Verify Step 1
            var stepDto1 = steps[0];
            stepDto1.Name.ShouldBe("Build Step");
            stepDto1.StepOrder.ShouldBe(1);
            stepDto1.StepType.ShouldBe("Action");
            stepDto1.Condition.ShouldBe("Success");
            stepDto1.Properties.ShouldContain(p => p.PropertyName == DeploymentVariables.Action.TargetRoles && p.PropertyValue == "build-server");
            stepDto1.Actions.Count.ShouldBe(1);

            var actionDto1 = stepDto1.Actions[0];
            actionDto1.Name.ShouldBe("Run Build");
            actionDto1.ActionType.ShouldBe("Octopus.Script");
            actionDto1.IsDisabled.ShouldBeFalse();
            actionDto1.IsRequired.ShouldBeTrue();
            actionDto1.Properties.ShouldContain(p => p.PropertyName == "Octopus.Action.Script.ScriptBody" && p.PropertyValue == "dotnet build");

            // Verify Step 2
            var stepDto2 = steps[1];
            stepDto2.Name.ShouldBe("Deploy Step");
            stepDto2.StepOrder.ShouldBe(2);
            stepDto2.Condition.ShouldBe("Variable");
            stepDto2.Properties.ShouldContain(p => p.PropertyName == DeploymentVariables.Action.TargetRoles && p.PropertyValue == "web-server");
            stepDto2.Actions.Count.ShouldBe(2);

            var actionDto2a = stepDto2.Actions[0];
            actionDto2a.Name.ShouldBe("Deploy App");
            actionDto2a.ActionType.ShouldBe("Octopus.KubernetesDeployContainers");
            actionDto2a.IsDisabled.ShouldBeFalse();
            actionDto2a.Properties.ShouldContain(p => p.PropertyName == "Octopus.Action.KubernetesContainers.Namespace" && p.PropertyValue == "production");

            var actionDto2b = stepDto2.Actions[1];
            actionDto2b.Name.ShouldBe("Notify Slack");
            actionDto2b.IsDisabled.ShouldBeTrue();
            actionDto2b.IsRequired.ShouldBeFalse();
        }).ConfigureAwait(false);
    }
}
