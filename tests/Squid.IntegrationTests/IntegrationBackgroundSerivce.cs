using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using Autofac;
using Moq;
using Squid.Core.Commands.Tentacle;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Deployments;
using Squid.Core.Services.Deployments.DeploymentCompletions;
using Squid.Core.Services.Deployments.ServerTask;
using Squid.Core.Services.Tentacle;
using Squid.Message.Enums;
using Environment = Squid.Core.Persistence.Entities.Deployments.Environment;
using Machine = Squid.Core.Persistence.Entities.Deployments.Machine;

namespace Squid.IntegrationTests;

[Collection("Sequential")]
public class IntegrationDeploymentTaskBackgroundService : TestBase<IntegrationDeploymentTaskBackgroundService>
{
    [Fact]
    public async Task RunAsync_ShouldProcessPendingDeploymentTask_AndMarkTaskSuccess()
    {
        var service = Resolve<DeploymentTaskBackgroundService>(RegisterTestHalibutAndGithubDownloader);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await PrepareDeploymentDataAsync().ConfigureAwait(false);

        await service.RunAsync(cts.Token).ConfigureAwait(false);

        await AssertTaskAndDeploymentCompletionAsync().ConfigureAwait(false);
    }

    private void RegisterTestHalibutAndGithubDownloader(ContainerBuilder builder)
    {
        var scriptServiceMock = new Mock<IAsyncScriptService>();

        scriptServiceMock
            .Setup(x => x.StartScriptAsync(It.IsAny<StartScriptCommand>()))
            .ReturnsAsync(new ScriptTicket(Guid.NewGuid().ToString("N")));

        scriptServiceMock
            .SetupSequence(x => x.GetStatusAsync(It.IsAny<ScriptStatusRequest>()))
            .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t1"), ProcessState.Running, 0, new List<ProcessOutput>(), 0))
            .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t1"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        scriptServiceMock
            .Setup(x => x.CompleteScriptAsync(It.IsAny<CompleteScriptCommand>()))
            .ReturnsAsync(new ScriptStatusResponse(new ScriptTicket("t1"), ProcessState.Complete, 0, new List<ProcessOutput>(), 0));

        builder.RegisterInstance(scriptServiceMock.Object)
            .As<IAsyncScriptService>()
            .SingleInstance();

        var securitySetting = new Squid.Core.Settings.Security.SecuritySetting
        {
            VariableEncryption = new Squid.Core.Settings.Security.VariableEncryptionDto
            {
                MasterKey = Convert.ToBase64String(new byte[32])
            }
        };

        builder.RegisterInstance(securitySetting)
            .As<Squid.Core.Settings.Security.SecuritySetting>()
            .SingleInstance();
    }

    private async Task PrepareDeploymentDataAsync()
    {
        var repository = Resolve<IRepository>();
        var unitOfWork = Resolve<IUnitOfWork>();

            var variableSet = new VariableSet
            {
                SpaceId = 1,
                OwnerType = VariableSetOwnerType.Project,
                OwnerId = 0,
                Version = 1,
                RelatedDocumentIds = string.Empty,
                LastModified = DateTimeOffset.UtcNow
            };

        await repository.InsertAsync(variableSet, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var variables = new List<Variable>
            {
                new()
                {
                    VariableSetId = variableSet.Id,
                    Name = "TestVariable",
                    Value = "TestValue",
                    Description = "Variable for integration test",
                    Type = VariableType.String,
                    IsSensitive = false,
                    SortOrder = 0,
                    LastModifiedOn = DateTimeOffset.UtcNow,
                    LastModifiedBy = "IntegrationTest"
                }
            };

        await repository.InsertAllAsync(variables, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var project = new Project
            {
                Name = "Test Project",
                Slug = "test-project",
                IsDisabled = false,
                VariableSetId = variableSet.Id,
                ProjectGroupId = 1,
                LifecycleId = 1,
                AutoCreateRelease = false,
                Json = string.Empty,
                IncludedLibraryVariableSetIds = string.Empty,
                DiscreteChannelRelease = false,
                DataVersion = Array.Empty<byte>(),
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
                AllowIgnoreChannelRules = false
            };

        await repository.InsertAsync(project, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            variableSet.OwnerId = project.Id;

        await repository.UpdateAsync(variableSet, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var process = new DeploymentProcess
            {
                Version = 1,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
                LastModifiedBy = "IntegrationTest"
            };

        await repository.InsertAsync(process, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            project.DeploymentProcessId = process.Id;

        await repository.UpdateAsync(project, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            var channel = new Channel
            {
                Name = "Default Channel",
                Description = "Default channel for integration test",
                ProjectId = project.Id,
                LifecycleId = project.LifecycleId,
                SpaceId = 1,
                Slug = "default-channel",
                IsDefault = true
            };

        await repository.InsertAsync(channel, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

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
                ClusterUrl = "https://172.16.145.222:6443",
                SkipTlsVerification = true,
                AccountId = 1,
                Namespace = "squid"
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

            var release = new Release
            {
                Version = "1.0.0",
                ProjectId = project.Id,
                ProjectVariableSetSnapshotId = 0,
                ProjectDeploymentProcessSnapshotId = 0,
                ChannelId = channel.Id,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow
            };

        await repository.InsertAsync(release, CancellationToken.None).ConfigureAwait(false);

        await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

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
                State = "Pending",
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
    }

    private async Task AssertTaskAndDeploymentCompletionAsync()
    {
        var taskDataProvider = Resolve<IServerTaskDataProvider>();
        var completionDataProvider = Resolve<IDeploymentCompletionDataProvider>();

        var tasks = await taskDataProvider.GetAllServerTasksAsync(CancellationToken.None).ConfigureAwait(false);

        tasks.ShouldNotBeNull();

        tasks.Count.ShouldBe(1);

        var task = tasks[0];

        task.State.ShouldBe("Success");

        var completions = await completionDataProvider.GetDeploymentCompletionsByDeploymentIdAsync(task.Id, CancellationToken.None).ConfigureAwait(false);

        completions.ShouldNotBeNull();

        completions.ShouldNotBeEmpty();

        foreach (var completion in completions)
        {
            completion.State.ShouldBe("Success");
        }
    }
}
