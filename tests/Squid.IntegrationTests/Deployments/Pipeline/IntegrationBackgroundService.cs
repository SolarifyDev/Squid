using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Halibut;
using Moq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.DeploymentExecution;
using Squid.Core.Services.DeploymentExecution.Kubernetes;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.IntegrationTests.Helpers;
using Squid.Message.Enums;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.IntegrationTests.Deployments.Pipeline;

public class IntegrationDeploymentTaskBackgroundService : DeploymentFixtureBase
{
    [Fact]
    public async Task RunAsync_ShouldProcessPendingDeploymentTask_AndMarkTaskSuccess()
    {
        await Run<IDeploymentTaskExecutor>(async executor =>
        {
            await PrepareDeploymentDataAsync().ConfigureAwait(false);

            await executor.ProcessAsync(1, CancellationToken.None).ConfigureAwait(false);

            await AssertTaskAndDeploymentCompletionAsync().ConfigureAwait(false);
        }, RegisterTestDependencies).ConfigureAwait(false);
    }

    private void RegisterTestDependencies(ContainerBuilder builder)
    {
        var executionStrategyMock = new Mock<IExecutionStrategy>();

        executionStrategyMock
            .Setup(x => x.ExecuteScriptAsync(It.IsAny<ScriptExecutionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ScriptExecutionResult
            {
                Success = true,
                LogLines = new List<string>(),
                ExitCode = 0
            });

        builder.Register(ctx =>
        {
            var contributor = ctx.Resolve<KubernetesApiEndpointVariableContributor>();
            var wrapper = ctx.Resolve<KubernetesApiScriptContextWrapper>();

            var transport = new Mock<IDeploymentTransport>();
            transport.Setup(t => t.CommunicationStyle).Returns(CommunicationStyle.KubernetesApi);
            transport.Setup(t => t.Variables).Returns(contributor);
            transport.Setup(t => t.ScriptWrapper).Returns(wrapper);
            transport.Setup(t => t.Strategy).Returns(executionStrategyMock.Object);

            var registry = new Mock<ITransportRegistry>();
            registry.Setup(r => r.Resolve(CommunicationStyle.KubernetesApi)).Returns(transport.Object);

            return registry.Object;
        })
        .As<ITransportRegistry>()
        .InstancePerLifetimeScope();

        var halibutFactoryMock = new Mock<IHalibutClientFactory>();
        builder.RegisterInstance(halibutFactoryMock.Object)
            .As<IHalibutClientFactory>()
            .SingleInstance();

    }

    private async Task PrepareDeploymentDataAsync()
    {
        await Run<IRepository, IUnitOfWork>(async (repository, unitOfWork) =>
        {
            var builder = new TestDataBuilder(repository, unitOfWork);

            var variableSet = await builder.CreateVariableSetAsync();
            await builder.CreateVariableAsync(variableSet.Id, "TestVariable", "TestValue");

            var project = await builder.CreateProjectAsync(variableSet.Id);
            await builder.UpdateVariableSetOwnerAsync(variableSet, project.Id);

            var process = await builder.CreateDeploymentProcessAsync();
            await builder.UpdateProjectProcessIdAsync(project, process.Id);

            var channel = await builder.CreateChannelAsync(project.Id, project.LifecycleId);

            var environment = new Environment
            {
                SpaceId = 1,
                Slug = "test-environment",
                Name = "Test Environment",
                Description = "Environment for integration test",
                SortOrder = 0,
                UseGuidedFailure = false,
                AllowDynamicInfrastructure = false,
                LastModifiedOn = DateTimeOffset.UtcNow,
                LastModifiedBy = "IntegrationTest"
            };

            await repository.InsertAsync(environment, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var endpointJson = JsonSerializer.Serialize(new
            {
                CommunicationStyle = "KubernetesApi",
                ClusterUrl = "https://172.16.145.222:6443",
                SkipTlsVerification = "True",
                Namespace = "squid",
                ResourceReferences = new[]
                {
                    new { Type = (int)EndpointResourceType.AuthenticationAccount, ResourceId = 1 }
                }
            });

            var machine = new Machine
            {
                Name = "Test Machine",
                IsDisabled = false,
                Roles = "web",
                EnvironmentIds = environment.Id.ToString(),
                Json = "{\"Endpoint\":{\"Uri\":\"https://localhost:10933\",\"Thumbprint\":\"TEST-THUMBPRINT\"}}",
                MachinePolicyId = null,
                Thumbprint = "TEST-THUMBPRINT",
                Uri = "https://172.16.145.222:10933",
                HasLatestCalamari = false,
                Endpoint = endpointJson,
                DataVersion = Array.Empty<byte>(),
                SpaceId = 1,
                OperatingSystem = OperatingSystemType.Windows,
                ShellName = "PowerShell",
                ShellVersion = "7.0",
                LicenseHash = string.Empty,
                Slug = "test-machine"
            };

            await repository.InsertAsync(machine, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var release = await builder.CreateReleaseAsync(project.Id, channel.Id, "1.0.0");

            var deployment = new Deployment
            {
                Name = "Test Deployment",
                SpaceId = 1,
                ChannelId = channel.Id,
                ProjectId = project.Id,
                ReleaseId = release.Id,
                EnvironmentId = environment.Id,
                DeployedBy = 1,
                Created = DateTimeOffset.UtcNow,
                Json = string.Empty
            };

            await repository.InsertAsync(deployment, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var serverTask = new ServerTask
            {
                Name = "Test Deployment Task",
                Description = "Integration test deployment task",
                QueueTime = DateTimeOffset.UtcNow,
                State = TaskState.Pending,
                ServerTaskType = "Deploy",
                ProjectId = project.Id,
                EnvironmentId = environment.Id,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
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

            await repository.InsertAsync(serverTask, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            deployment.TaskId = serverTask.Id;

            await repository.UpdateAsync(deployment, CancellationToken.None).ConfigureAwait(false);
            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private async Task AssertTaskAndDeploymentCompletionAsync()
    {
        await Run<IServerTaskDataProvider, IDeploymentCompletionDataProvider>(async (taskDataProvider, completionDataProvider) =>
        {
            var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);

            tasks.ShouldNotBeNull();

            tasks.Count.ShouldBe(1);

            var task = tasks[0];

            task.State.ShouldBe(TaskState.Success);

            var completions = await completionDataProvider.GetDeploymentCompletionsByDeploymentIdAsync(task.Id, CancellationToken.None).ConfigureAwait(false);

            completions.ShouldNotBeNull();

            completions.ShouldNotBeEmpty();

            foreach (var completion in completions)
            {
                completion.State.ShouldBe(TaskState.Success);
            }
        }).ConfigureAwait(false);
    }
}
