using System.Collections.Generic;
using System.Linq;
using Squid.Core.Persistence.Db;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Enums;
using Squid.Message.Enums.Deployments;

namespace Squid.IntegrationTests.Helpers;

public class TestDataBuilder
{
    private readonly IRepository _repository;
    private readonly IUnitOfWork _unitOfWork;

    public TestDataBuilder(IRepository repository, IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _unitOfWork = unitOfWork;
    }

    public async Task<VariableSet> CreateVariableSetAsync(
        VariableSetOwnerType ownerType = VariableSetOwnerType.Project,
        int ownerId = 0)
    {
        var entity = new VariableSet
        {
            SpaceId = 1,
            OwnerType = ownerType,
            OwnerId = ownerId,
            Version = 1,
            RelatedDocumentIds = string.Empty,
            LastModified = DateTimeOffset.UtcNow
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<Variable> CreateVariableAsync(
        int variableSetId,
        string name,
        string value,
        VariableType type = VariableType.String,
        bool isSensitive = false)
    {
        var entity = new Variable
        {
            VariableSetId = variableSetId,
            Name = name,
            Value = value,
            Description = $"Variable {name}",
            Type = type,
            IsSensitive = isSensitive,
            SortOrder = 0,
            LastModifiedOn = DateTimeOffset.UtcNow,
            LastModifiedBy = "IntegrationTest"
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<List<Variable>> CreateVariablesAsync(
        int variableSetId,
        params (string Name, string Value, VariableType Type, bool IsSensitive)[] definitions)
    {
        var variables = definitions.Select(d => new Variable
        {
            VariableSetId = variableSetId,
            Name = d.Name,
            Value = d.Value,
            Description = $"Variable {d.Name}",
            Type = d.Type,
            IsSensitive = d.IsSensitive,
            SortOrder = 0,
            LastModifiedOn = DateTimeOffset.UtcNow,
            LastModifiedBy = "IntegrationTest"
        }).ToList();

        await _repository.InsertAllAsync(variables).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return variables;
    }

    public async Task<Project> CreateProjectAsync(int variableSetId, int deploymentProcessId = 0)
    {
        var entity = new Project
        {
            Name = "Test Project",
            Slug = "test-project",
            IsDisabled = false,
            VariableSetId = variableSetId,
            DeploymentProcessId = deploymentProcessId,
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

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        var defaultChannel = new Channel
        {
            Name = "Default",
            ProjectId = entity.Id,
            SpaceId = 1,
            IsDefault = true,
            Slug = "default"
        };

        await _repository.InsertAsync(defaultChannel).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        return entity;
    }

    public async Task<ProjectGroup> CreateProjectGroupAsync(string name = "Default Project Group")
    {
        var entity = new ProjectGroup
        {
            Name = name,
            Description = string.Empty,
            SpaceId = 1,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            DataVersion = Array.Empty<byte>()
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<DeploymentProcess> CreateDeploymentProcessAsync()
    {
        var entity = new DeploymentProcess
        {
            Version = 1,
            SpaceId = 1,
            LastModified = DateTimeOffset.UtcNow,
            LastModifiedBy = "IntegrationTest"
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<DeploymentStep> CreateDeploymentStepAsync(
        int processId,
        int stepOrder,
        string name,
        string stepType = "Action",
        string condition = "Success",
        bool isRequired = true)
    {
        var entity = new DeploymentStep
        {
            ProcessId = processId,
            StepOrder = stepOrder,
            Name = name,
            StepType = stepType,
            Condition = condition,
            StartTrigger = "",
            PackageRequirement = "",
            IsDisabled = false,
            IsRequired = isRequired,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<DeploymentAction> CreateDeploymentActionAsync(
        int stepId,
        int actionOrder,
        string name,
        string actionType = "Octopus.Script",
        bool isDisabled = false,
        bool isRequired = true)
    {
        var entity = new DeploymentAction
        {
            StepId = stepId,
            ActionOrder = actionOrder,
            Name = name,
            ActionType = actionType,
            IsDisabled = isDisabled,
            IsRequired = isRequired,
            CanBeUsedForProjectVersioning = false,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task CreateActionPropertiesAsync(int actionId, params (string Name, string Value)[] properties)
    {
        var entities = properties.Select(p => new DeploymentActionProperty
        {
            ActionId = actionId,
            PropertyName = p.Name,
            PropertyValue = p.Value
        }).ToList();

        await _repository.InsertAllAsync(entities).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task CreateStepPropertiesAsync(int stepId, params (string Name, string Value)[] properties)
    {
        var entities = properties.Select(p => new DeploymentStepProperty
        {
            StepId = stepId,
            PropertyName = p.Name,
            PropertyValue = p.Value
        }).ToList();

        await _repository.InsertAllAsync(entities).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task CreateActionEnvironmentsAsync(int actionId, params int[] environmentIds)
    {
        var entities = environmentIds.Select(envId => new ActionEnvironment
        {
            ActionId = actionId,
            EnvironmentId = envId
        }).ToList();

        await _repository.InsertAllAsync(entities).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task CreateActionChannelsAsync(int actionId, params int[] channelIds)
    {
        var entities = channelIds.Select(chId => new ActionChannel
        {
            ActionId = actionId,
            ChannelId = chId
        }).ToList();

        await _repository.InsertAllAsync(entities).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task CreateActionMachineRolesAsync(int actionId, params string[] roles)
    {
        var entities = roles.Select(role => new ActionMachineRole
        {
            ActionId = actionId,
            MachineRole = role
        }).ToList();

        await _repository.InsertAllAsync(entities).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task<Channel> CreateChannelAsync(int projectId, int lifecycleId = 1)
    {
        var entity = new Channel
        {
            Name = "Default Channel",
            Description = "Default channel for integration test",
            ProjectId = projectId,
            LifecycleId = lifecycleId,
            SpaceId = 1,
            Slug = "default-channel",
            IsDefault = true
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<Release> CreateReleaseAsync(int projectId, int channelId, string version = "1.0.0")
    {
        var entity = new Release
        {
            Version = version,
            ProjectId = projectId,
            ProjectVariableSetSnapshotId = 0,
            ProjectDeploymentProcessSnapshotId = 0,
            ChannelId = channelId,
            SpaceId = 1,
            LastModified = DateTimeOffset.UtcNow
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<Squid.Core.Persistence.Entities.Deployments.Environment> CreateEnvironmentAsync(string name = "Test Environment")
    {
        var entity = new Squid.Core.Persistence.Entities.Deployments.Environment
        {
            Name = name,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            SpaceId = 1,
            SortOrder = 0
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<Machine> CreateMachineAsync(int environmentId, string name = "Test Machine", string roles = "web-server")
    {
        var entity = new Machine
        {
            Name = name,
            IsDisabled = false,
            Roles = roles,
            EnvironmentIds = environmentId.ToString(),
            SpaceId = 1,
            Endpoint = "{}",
            Json = "{}",
            DataVersion = Array.Empty<byte>(),
            ShellName = string.Empty,
            ShellVersion = string.Empty,
            LicenseHash = string.Empty,
            Slug = name.ToLowerInvariant().Replace(" ", "-")
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task UpdateProjectProcessIdAsync(Project project, int processId)
    {
        project.DeploymentProcessId = processId;
        await _repository.UpdateAsync(project).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateVariableSetOwnerAsync(VariableSet variableSet, int ownerId)
    {
        variableSet.OwnerId = ownerId;
        await _repository.UpdateAsync(variableSet).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task<Lifecycle> CreateLifecycleAsync(string name = "Default Lifecycle")
    {
        var entity = new Lifecycle
        {
            Name = name,
            SpaceId = 1,
            Slug = name.ToLowerInvariant().Replace(" ", "-"),
            ReleaseRetentionKeepForever = true,
            TentacleRetentionKeepForever = true
        };

        await _repository.InsertAsync(entity).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);
        return entity;
    }

    public async Task<LifecyclePhase> CreateLifecyclePhaseAsync(int lifecycleId, int environmentId, string name = "Default Phase")
    {
        var phase = new LifecyclePhase
        {
            LifecycleId = lifecycleId,
            Name = name,
            SortOrder = 0,
            IsOptionalPhase = false
        };

        await _repository.InsertAsync(phase).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        var phaseEnv = new LifecyclePhaseEnvironment
        {
            PhaseId = phase.Id,
            EnvironmentId = environmentId,
            TargetType = LifecyclePhaseEnvironmentTargetType.Automatic
        };

        await _repository.InsertAsync(phaseEnv).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync().ConfigureAwait(false);

        return phase;
    }
}
