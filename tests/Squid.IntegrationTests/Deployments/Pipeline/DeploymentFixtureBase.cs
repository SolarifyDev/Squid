using System.Text.Json;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution.Transport;
using Squid.Core.Services.Deployments.Checkpoints;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Constants;
using Squid.Message.Enums;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.IntegrationTests.Deployments.Pipeline;

[Collection("Deployment Tests")]
public class DeploymentFixtureBase : TestBase
{
    protected DeploymentFixtureBase() : base("_deployment_", "squid_test_deployment") { }

    protected async Task<SeededDeployment> SeedScriptDeploymentAsync(bool isRequired, int machineCount = 1)
    {
        SeededDeployment? result = null;

        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync().ConfigureAwait(false);
            var project = await builder.CreateProjectAsync(variableSet.Id).ConfigureAwait(false);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id).ConfigureAwait(false);

            var process = await builder.CreateDeploymentProcessAsync().ConfigureAwait(false);
            await builder.UpdateProjectProcessIdAsync(project, process.Id).ConfigureAwait(false);

            var step = await builder.CreateDeploymentStepAsync(process.Id, 1, "Run script", isRequired: isRequired).ConfigureAwait(false);
            await builder.CreateStepPropertiesAsync(step.Id, ("Squid.Action.TargetRoles", "web")).ConfigureAwait(false);

            var action = await builder.CreateDeploymentActionAsync(step.Id, 1, "Run script", actionType: SpecialVariables.ActionTypes.Script).ConfigureAwait(false);
            await builder.CreateActionMachineRolesAsync(action.Id, "web").ConfigureAwait(false);
            await builder.CreateActionPropertiesAsync(action.Id,
                ("Squid.Action.Script.ScriptBody", "echo transient-pause-test"),
                ("Squid.Action.Script.Syntax", "Bash")).ConfigureAwait(false);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId).ConfigureAwait(false);

            var environment = new Environment
            {
                SpaceId = 1,
                Slug = $"transient-env-{Guid.NewGuid():N}"[..20],
                Name = "Transient Pause Env",
                Description = "Environment for transient-pause E2E",
                SortOrder = 0,
                UseGuidedFailure = false,
                AllowDynamicInfrastructure = false,
                LastModifiedDate = DateTimeOffset.UtcNow,
                LastModifiedBy = 0
            };

            await repository.InsertAsync(environment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // KubernetesAgent so the REAL HalibutMachineExecutionStrategy is used
            // (it records the in-flight pointer); the faked client makes GetStatus throw.
            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesAgent",
                SubscriptionId = $"sub-{Guid.NewGuid():N}",
                Thumbprint = "AA11BB22CC33DD44",
                Namespace = "default"
            });

            var machine = new Machine
            {
                Name = $"Transient Pause Agent {Guid.NewGuid():N}",
                IsDisabled = false,
                Roles = "[\"web\"]",
                EnvironmentIds = $"[{environment.Id}]",
                Endpoint = endpointJson,
                SpaceId = 1,
                Slug = $"transient-agent-{Guid.NewGuid():N}"[..20]
            };

            await repository.InsertAsync(machine).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // Extra targets sharing the first one's role/environment (a required step fans out across all).
            for (var i = 2; i <= machineCount; i++)
            {
                await repository.InsertAsync(new Machine
                {
                    Name = $"Transient Pause Agent {i} {Guid.NewGuid():N}",
                    IsDisabled = false,
                    Roles = machine.Roles,
                    EnvironmentIds = machine.EnvironmentIds,
                    Endpoint = JsonSerializer.Serialize(new { CommunicationStyle = "KubernetesAgent", SubscriptionId = $"sub-{Guid.NewGuid():N}", Thumbprint = "AA11BB22CC33DD44", Namespace = "default" }),
                    SpaceId = 1,
                    Slug = $"transient-agent-{Guid.NewGuid():N}"[..20]
                }).ConfigureAwait(false);
            }

            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0").ConfigureAwait(false);

            var deployment = new Deployment
            {
                Name = "Transient Pause Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                CreatedDate = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "Transient Pause Task",
                Description = "Transient-pause E2E deployment task",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                SpaceId = 1,
                LastModifiedDate = DateTimeOffset.UtcNow,
                BusinessProcessState = "Queued",
                StateOrder = 1,
                Weight = 1,
                BatchId = 0,
                JSON = string.Empty,
                HasWarningsOrErrors = false,
                ServerNodeId = Guid.NewGuid(),
                DurationSeconds = 0,
                DataVersion = Array.Empty<byte>()
            };

            await repository.InsertAsync(serverTask).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;
            await repository.UpdateAsync(deployment).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync().ConfigureAwait(false);

            // The snapshot preserves the entity step/action IDs (DeploymentSnapshotService
            // copies step.Id / action.Id verbatim), and the strategy records the pointer
            // keyed by (Machine.Id, request.StepId, request.ActionId) — so the dispatch
            // slot the production code writes is exactly these seeded IDs.
            result = new SeededDeployment(serverTask.Id, new DispatchSlot(machine.Id, step.Id, action.Id));
        }).ConfigureAwait(false);

        return result!;
    }

    protected sealed record SeededDeployment(int TaskId, DispatchSlot DispatchSlot);
}
