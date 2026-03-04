using Squid.Core.Persistence.Db;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.Deployments.Snapshots;
using Squid.IntegrationTests.Helpers;

namespace Squid.IntegrationTests.Deployments.Snapshots;

public class IntegrationProcessSnapshot : SnapshotFixtureBase
{
    [Fact]
    public async Task SnapshotProcessFromIdAsync_SingleStepSingleAction_SavesCompressedSnapshot()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Deploy Step", "Action", "Success");
            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Run Script", "Octopus.Script");
            await builder.CreateActionPropertiesAsync(action.Id, ("Octopus.Action.Script.ScriptBody", "echo hello"));
            await builder.CreateStepPropertiesAsync(step.Id, (DeploymentVariables.Action.TargetRoles, "web"));
            await builder.CreateActionEnvironmentsAsync(action.Id, 10, 20);
            await builder.CreateActionChannelsAsync(action.Id, 5);
            await builder.CreateActionMachineRolesAsync(action.Id, "web-server", "api-server");

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            snapshot.ShouldNotBeNull();
            snapshot.Id.ShouldBeGreaterThan(0);
            snapshot.OriginalProcessId.ShouldBe(process.Id);
            snapshot.Data.ShouldNotBeNull();
            snapshot.Data.StepSnapshots.Count.ShouldBe(1);

            var stepSnap = snapshot.Data.StepSnapshots[0];
            stepSnap.Name.ShouldBe("Deploy Step");
            stepSnap.StepOrder.ShouldBe(1);
            stepSnap.Properties.ShouldContainKey(DeploymentVariables.Action.TargetRoles);
            stepSnap.Properties[DeploymentVariables.Action.TargetRoles].ShouldBe("web");

            stepSnap.ActionSnapshots.Count.ShouldBe(1);
            var actionSnap = stepSnap.ActionSnapshots[0];
            actionSnap.Name.ShouldBe("Run Script");
            actionSnap.ActionType.ShouldBe("Octopus.Script");
            actionSnap.Properties.ShouldContainKey("Octopus.Action.Script.ScriptBody");
            actionSnap.Properties["Octopus.Action.Script.ScriptBody"].ShouldBe("echo hello");
            actionSnap.Environments.OrderBy(x => x).ShouldBe(new List<int> { 10, 20 });
            actionSnap.Channels.ShouldBe(new List<int> { 5 });
            actionSnap.MachineRoles.OrderBy(x => x).ShouldBe(new List<string> { "api-server", "web-server" });
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task LoadProcessSnapshotAsync_RoundTrip_DataMatchesOriginal()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Step A");
            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Action A");
            await builder.CreateActionPropertiesAsync(action.Id, ("Key1", "Val1"));

            var created = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
            var loaded = await snapshotService.LoadProcessSnapshotAsync(created.Id);

            loaded.ShouldNotBeNull();
            loaded.Id.ShouldBe(created.Id);
            loaded.OriginalProcessId.ShouldBe(created.OriginalProcessId);
            loaded.Data.StepSnapshots.Count.ShouldBe(created.Data.StepSnapshots.Count);

            var loadedStep = loaded.Data.StepSnapshots[0];
            var createdStep = created.Data.StepSnapshots[0];
            loadedStep.Name.ShouldBe(createdStep.Name);
            loadedStep.ActionSnapshots[0].Properties["Key1"].ShouldBe("Val1");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_EmptyProcess_ReturnsValidEmptySnapshot()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            snapshot.ShouldNotBeNull();
            snapshot.Id.ShouldBeGreaterThan(0);
            snapshot.OriginalProcessId.ShouldBe(process.Id);
            snapshot.Data.ShouldNotBeNull();
            snapshot.Data.StepSnapshots.ShouldBeEmpty();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_MultiStepMultiAction_PreservesFullHierarchy()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();

            for (var s = 1; s <= 3; s++)
            {
                var step = await builder.CreateDeploymentStepAsync(process.Id, s, $"Step {s}");
                await builder.CreateStepPropertiesAsync(step.Id, ($"StepProp{s}", $"StepVal{s}"));

                for (var a = 1; a <= 2; a++)
                {
                    var action = await builder.CreateDeploymentActionAsync(step.Id, a, $"Action {s}.{a}");
                    await builder.CreateActionPropertiesAsync(action.Id, ($"ActionProp{s}{a}", $"ActionVal{s}{a}"));
                    await builder.CreateActionEnvironmentsAsync(action.Id, s * 10 + a);
                    await builder.CreateActionChannelsAsync(action.Id, s * 100 + a);
                    await builder.CreateActionMachineRolesAsync(action.Id, $"role-{s}-{a}");
                }
            }

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            snapshot.Data.StepSnapshots.Count.ShouldBe(3);

            for (var s = 0; s < 3; s++)
            {
                var stepSnap = snapshot.Data.StepSnapshots[s];
                stepSnap.Name.ShouldBe($"Step {s + 1}");
                stepSnap.StepOrder.ShouldBe(s + 1);
                stepSnap.Properties.ShouldContainKey($"StepProp{s + 1}");
                stepSnap.ActionSnapshots.Count.ShouldBe(2);

                for (var a = 0; a < 2; a++)
                {
                    var actionSnap = stepSnap.ActionSnapshots[a];
                    actionSnap.Name.ShouldBe($"Action {s + 1}.{a + 1}");
                    actionSnap.ActionOrder.ShouldBe(a + 1);
                    actionSnap.Properties.ShouldContainKey($"ActionProp{s + 1}{a + 1}");
                    actionSnap.Environments.ShouldContain((s + 1) * 10 + (a + 1));
                    actionSnap.Channels.ShouldContain((s + 1) * 100 + (a + 1));
                    actionSnap.MachineRoles.ShouldContain($"role-{s + 1}-{a + 1}");
                }
            }
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromReleaseAsync_FollowsReleaseProjectProcessChain()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            var project = await builder.CreateProjectAsync(variableSet.Id);
            var process = await builder.CreateDeploymentProcessAsync();
            await builder.UpdateProjectProcessIdAsync(project, process.Id);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Release Step");
            await builder.CreateDeploymentActionAsync(step.Id, 1, "Release Action");

            var channel = await builder.CreateChannelAsync(project.Id);
            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "2.0.0");

            var snapshot = await snapshotService.SnapshotProcessFromReleaseAsync(release);

            snapshot.ShouldNotBeNull();
            snapshot.OriginalProcessId.ShouldBe(process.Id);
            snapshot.Data.StepSnapshots.Count.ShouldBe(1);
            snapshot.Data.StepSnapshots[0].Name.ShouldBe("Release Step");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_DisabledAction_PreservesIsDisabled()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Step With Disabled");
            await builder.CreateDeploymentActionAsync(step.Id, 1, "Enabled Action", isDisabled: false);
            await builder.CreateDeploymentActionAsync(step.Id, 2, "Disabled Action", isDisabled: true);

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            var actions = snapshot.Data.StepSnapshots[0].ActionSnapshots;
            actions.Count.ShouldBe(2);
            actions[0].IsDisabled.ShouldBeFalse();
            actions[1].IsDisabled.ShouldBeTrue();
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_StepOrdering_PreservesOrder()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            await builder.CreateDeploymentStepAsync(process.Id, 3, "Third Step");
            await builder.CreateDeploymentStepAsync(process.Id, 1, "First Step");
            await builder.CreateDeploymentStepAsync(process.Id, 2, "Second Step");

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            snapshot.Data.StepSnapshots.Count.ShouldBe(3);
            snapshot.Data.StepSnapshots[0].Name.ShouldBe("First Step");
            snapshot.Data.StepSnapshots[0].StepOrder.ShouldBe(1);
            snapshot.Data.StepSnapshots[1].Name.ShouldBe("Second Step");
            snapshot.Data.StepSnapshots[1].StepOrder.ShouldBe(2);
            snapshot.Data.StepSnapshots[2].Name.ShouldBe("Third Step");
            snapshot.Data.StepSnapshots[2].StepOrder.ShouldBe(3);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_ActionOrdering_PreservesOrder()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Ordered Step");
            await builder.CreateDeploymentActionAsync(step.Id, 2, "Second Action");
            await builder.CreateDeploymentActionAsync(step.Id, 1, "First Action");
            await builder.CreateDeploymentActionAsync(step.Id, 3, "Third Action");

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            var actions = snapshot.Data.StepSnapshots[0].ActionSnapshots;
            actions.Count.ShouldBe(3);
            actions[0].Name.ShouldBe("First Action");
            actions[0].ActionOrder.ShouldBe(1);
            actions[1].Name.ShouldBe("Second Action");
            actions[1].ActionOrder.ShouldBe(2);
            actions[2].Name.ShouldBe("Third Action");
            actions[2].ActionOrder.ShouldBe(3);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_Idempotency_SameDataProducesSameHash()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Idempotent Step");
            await builder.CreateDeploymentActionAsync(step.Id, 1, "Idempotent Action");

            var snapshot1 = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
            var snapshot2 = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            snapshot1.Id.ShouldBe(snapshot2.Id);
            snapshot1.Data.StepSnapshots.Count.ShouldBe(snapshot2.Data.StepSnapshots.Count);
            snapshot1.Data.StepSnapshots[0].Name.ShouldBe(snapshot2.Data.StepSnapshots[0].Name);
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_Isolation_ModifySourceAfterSnapshot_SnapshotUnchanged()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Original Step");
            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Original Action");
            await builder.CreateActionPropertiesAsync(action.Id, ("Script", "echo original"));

            var snapshot = await snapshotService.SnapshotProcessFromIdAsync(process.Id);
            var snapshotId = snapshot.Id;

            // Modify source: rename step, add new action, change property
            step.Name = "Modified Step";
            await repository.UpdateAsync(step).ConfigureAwait(false);
            await builder.CreateDeploymentActionAsync(step.Id, 2, "New Action After Snapshot");
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // Reload snapshot — must still reflect original data
            var reloaded = await snapshotService.LoadProcessSnapshotAsync(snapshotId);

            reloaded.Data.StepSnapshots.Count.ShouldBe(1);
            reloaded.Data.StepSnapshots[0].Name.ShouldBe("Original Step");
            reloaded.Data.StepSnapshots[0].ActionSnapshots.Count.ShouldBe(1);
            reloaded.Data.StepSnapshots[0].ActionSnapshots[0].Name.ShouldBe("Original Action");
            reloaded.Data.StepSnapshots[0].ActionSnapshots[0].Properties["Script"].ShouldBe("echo original");
        }).ConfigureAwait(false);
    }

    [Fact]
    public async Task SnapshotProcessFromIdAsync_DeduplicationBreaksOnChange_NewSnapshotCreated()
    {
        await Run<IRepository, IUnitOfWork, IDeploymentSnapshotService>(async (repository, unitOfWork, snapshotService) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var process = await builder.CreateDeploymentProcessAsync();
            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Step V1");
            await builder.CreateDeploymentActionAsync(step.Id, 1, "Action V1");

            var snapshotV1 = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            // Modify source data
            await builder.CreateDeploymentActionAsync(step.Id, 2, "Action V2");

            var snapshotV2 = await snapshotService.SnapshotProcessFromIdAsync(process.Id);

            // Different content → different snapshot
            snapshotV2.Id.ShouldNotBe(snapshotV1.Id);
            snapshotV2.Data.StepSnapshots[0].ActionSnapshots.Count.ShouldBe(2);

            // Original snapshot unchanged
            var reloadedV1 = await snapshotService.LoadProcessSnapshotAsync(snapshotV1.Id);
            reloadedV1.Data.StepSnapshots[0].ActionSnapshots.Count.ShouldBe(1);
        }).ConfigureAwait(false);
    }
}
