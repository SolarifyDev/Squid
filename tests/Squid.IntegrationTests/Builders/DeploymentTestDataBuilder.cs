using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Squid.Core.Persistence;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;

namespace Squid.IntegrationTests.Builders;

public class DeploymentTestDataBuilder
{
    private readonly List<Func<IRepository, Task>> _actions = new();
    private int _projectId;
    private int _variableSetId;
    private int _environmentId;
    private int _channelId;
    private int _machineId;
    private int _releaseId;
    private int _deploymentId;
    private int _taskId;

    public int ProjectId => _projectId;
    public int VariableSetId => _variableSetId;
    public int EnvironmentId => _environmentId;
    public int ChannelId => _channelId;
    public int MachineId => _machineId;
    public int ReleaseId => _releaseId;
    public int DeploymentId => _deploymentId;
    public int TaskId => _taskId;

    public DeploymentTestDataBuilder WithVariableSet(Action<VariableSet>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var variableSet = new VariableSet
            {
                SpaceId = 1,
                OwnerType = VariableSetOwnerType.Project,
                OwnerId = 0,
                Version = 1,
                RelatedDocumentIds = string.Empty,
                LastModified = DateTimeOffset.UtcNow
            };
            configure?.Invoke(variableSet);
            await repository.InsertAsync(variableSet);
            _variableSetId = variableSet.Id;
        });
        return this;
    }

    public DeploymentTestDataBuilder WithProject(Action<Project>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var project = new Project
            {
                Name = "Test Project",
                Slug = "test-project",
                IsDisabled = false,
                VariableSetId = _variableSetId,
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
            configure?.Invoke(project);
            await repository.InsertAsync(project);
            _projectId = project.Id;

            if (_variableSetId > 0)
            {
                var variableSet = await repository.GetByIdAsync<VariableSet>(_variableSetId);
                variableSet!.OwnerId = project.Id;
                await repository.UpdateAsync(variableSet);
            }
        });
        return this;
    }

    public DeploymentTestDataBuilder WithDeploymentProcess(Action<DeploymentProcess>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var process = new DeploymentProcess
            {
                Version = 1,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow,
                LastModifiedBy = "IntegrationTest"
            };
            configure?.Invoke(process);
            await repository.InsertAsync(process);

            if (_projectId > 0)
            {
                var project = await repository.GetByIdAsync<Project>(_projectId);
                project!.DeploymentProcessId = process.Id;
                await repository.UpdateAsync(project);
            }
        });
        return this;
    }

    public DeploymentTestDataBuilder WithLifecycle(Action<Lifecycle>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var retentionPolicy = new RetentionPolicy
            {
                QuantityToKeep = 10,
                ShouldKeepForever = false,
                Unit = RetentionPolicyUnit.Days
            };
            await repository.InsertAsync(retentionPolicy);

            var lifecycle = new Lifecycle
            {
                Name = "Default Lifecycle",
                SpaceId = 1,
                ReleaseRetentionPolicyId = retentionPolicy.Id,
                TentacleRetentionPolicyId = retentionPolicy.Id
            };
            configure?.Invoke(lifecycle);
            await repository.InsertAsync(lifecycle);

            if (_projectId > 0)
            {
                var project = await repository.GetByIdAsync<Project>(_projectId);
                project!.LifecycleId = lifecycle.Id;
                await repository.UpdateAsync(project);
            }
        });
        return this;
    }

    public DeploymentTestDataBuilder WithChannel(Action<Channel>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var channel = new Channel
            {
                Name = "Default Channel",
                Description = "Default channel for integration test",
                ProjectId = _projectId,
                LifecycleId = 1,
                SpaceId = 1,
                Slug = "default-channel",
                IsDefault = true
            };
            configure?.Invoke(channel);
            await repository.InsertAsync(channel);
            _channelId = channel.Id;
        });
        return this;
    }

    public DeploymentTestDataBuilder WithEnvironment(Action<Squid.Core.Persistence.Entities.Deployments.Environment>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var environment = new Squid.Core.Persistence.Entities.Deployments.Environment
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
            configure?.Invoke(environment);
            await repository.InsertAsync(environment);
            _environmentId = environment.Id;
        });
        return this;
    }

    public DeploymentTestDataBuilder WithMachine(Action<Machine>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var endpointJson = """
                {
                    "ClusterUrl": "https://kubernetes:6443",
                    "SkipTlsVerification": true,
                    "AccountId": "1",
                    "Namespace": "squid"
                }
                """;

            var machine = new Machine
            {
                Name = "Test Machine",
                IsDisabled = false,
                Roles = "web",
                EnvironmentIds = _environmentId.ToString(),
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
            configure?.Invoke(machine);
            await repository.InsertAsync(machine);
            _machineId = machine.Id;
        });
        return this;
    }

    public DeploymentTestDataBuilder WithRelease(Action<Release>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var release = new Release
            {
                Version = "1.0.0",
                ProjectId = _projectId,
                ProjectVariableSetSnapshotId = 0,
                ProjectDeploymentProcessSnapshotId = 0,
                ChannelId = _channelId > 0 ? _channelId : 1,
                SpaceId = 1,
                LastModified = DateTimeOffset.UtcNow
            };
            configure?.Invoke(release);
            await repository.InsertAsync(release);
            _releaseId = release.Id;
        });
        return this;
    }

    public DeploymentTestDataBuilder WithDeployment(Action<Deployment>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var deployment = new Deployment
            {
                Name = "Test Deployment",
                SpaceId = 1,
                ChannelId = _channelId > 0 ? _channelId : 1,
                ProjectId = _projectId,
                ReleaseId = _releaseId,
                EnvironmentId = _environmentId,
                DeployedBy = 1,
                Created = DateTimeOffset.UtcNow,
                Json = string.Empty
            };
            configure?.Invoke(deployment);
            await repository.InsertAsync(deployment);
            _deploymentId = deployment.Id;
        });
        return this;
    }

    public DeploymentTestDataBuilder WithServerTask(Action<ServerTask>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var serverTask = new ServerTask
            {
                Name = "Test Deployment Task",
                Description = "Integration test deployment task",
                QueueTime = DateTimeOffset.UtcNow,
                State = "Pending",
                ServerTaskType = "Deploy",
                ProjectId = _projectId,
                EnvironmentId = _environmentId,
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
            configure?.Invoke(serverTask);
            await repository.InsertAsync(serverTask);
            _taskId = serverTask.Id;

            if (_deploymentId > 0)
            {
                var deployment = await repository.GetByIdAsync<Deployment>(_deploymentId);
                deployment!.TaskId = serverTask.Id;
                await repository.UpdateAsync(deployment);
            }
        });
        return this;
    }

    public DeploymentTestDataBuilder WithVariables(Action<List<Variable>>? configure = null)
    {
        _actions.Add(async repository =>
        {
            var variables = new List<Variable>
            {
                new()
                {
                    VariableSetId = _variableSetId,
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
            configure?.Invoke(variables);
            await repository.InsertAllAsync(variables);
        });
        return this;
    }

    public async Task BuildAsync(IRepository repository)
    {
        foreach (var action in _actions)
        {
            await action(repository);
        }
    }

    public static DeploymentTestDataBuilder CreateDefault()
    {
        return new DeploymentTestDataBuilder()
            .WithVariableSet()
            .WithProject()
            .WithDeploymentProcess()
            .WithLifecycle()
            .WithChannel()
            .WithEnvironment()
            .WithMachine()
            .WithRelease()
            .WithDeployment()
            .WithServerTask()
            .WithVariables();
    }
}
