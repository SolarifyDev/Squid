using System.Text.Json;
using FluentValidation;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Message.Commands.ProjectTriggers;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Management;

public sealed partial class ProjectTriggerManagementService
{
    public async Task<ProjectTriggerChangeDto> CreateAsync(CreateProjectTriggerCommand command, CancellationToken ct)
    {
        var project = await ProjectAsync(command, Permission.ProjectEdit, ct).ConfigureAwait(false);
        var definition = Definition(project, command);

        await EnsureExecutableAsync(definition, ct).ConfigureAwait(false);

        var trigger = BuildTrigger(project, definition, command);

        await _projectTriggerDataProvider.AddAsync(trigger, ct).ConfigureAwait(false);

        return Change(trigger);
    }

    public async Task<ProjectTriggerChangeDto> UpdateAsync(UpdateProjectTriggerCommand command, CancellationToken ct)
    {
        var project = await ProjectAsync(command, Permission.ProjectEdit, ct).ConfigureAwait(false);
        var trigger = await LoadRevisionAsync(project, command.TriggerId, command.ExpectedRevision, ct)
            .ConfigureAwait(false);

        if (!IsAvailable(trigger.TriggerType, trigger.ActionType))
            throw new ValidationException("The stored trigger type and action are not available for editing.");

        var definition = Definition(project, command);

        await EnsureExecutableAsync(definition, ct).ConfigureAwait(false);

        var executionChanged = trigger.TriggerType != definition.TriggerType
            || trigger.ActionType != definition.ActionType
            || !SameConfiguration(trigger.ConfigurationJson, command.Configuration);

        if (executionChanged) UpdateExecution(trigger, definition);

        trigger.Name = command.Name.Trim();
        trigger.Description = command.Description;

        return await SaveRevisionAsync(trigger, executionChanged, ct).ConfigureAwait(false);
    }

    public async Task<ProjectTriggerChangeDto> EnableAsync(EnableProjectTriggerCommand command, CancellationToken ct)
    {
        var trigger = await LoadRevisionAsync(command, ct).ConfigureAwait(false);
        var definition = new ProjectTriggerDefinition(
            trigger.ProjectId, trigger.SpaceId, trigger.TriggerType, trigger.ActionType, trigger.ConfigurationJson);

        await EnsureExecutableAsync(definition, ct).ConfigureAwait(false);

        if (!trigger.IsDisabled) return Change(trigger);

        trigger.ExecutionUserId = UserId;
        trigger.IsDisabled = false;

        return await SaveRevisionAsync(trigger, resetState: true, ct).ConfigureAwait(false);
    }

    public async Task<ProjectTriggerChangeDto> DisableAsync(DisableProjectTriggerCommand command, CancellationToken ct)
    {
        var trigger = await LoadRevisionAsync(command, ct).ConfigureAwait(false);

        if (trigger.IsDisabled) return Change(trigger);

        trigger.IsDisabled = true;

        return await SaveRevisionAsync(trigger, resetState: false, ct).ConfigureAwait(false);
    }

    public async Task<ProjectTriggerChangeDto> DeleteAsync(DeleteProjectTriggerCommand command, CancellationToken ct)
    {
        var trigger = await LoadRevisionAsync(command, ct).ConfigureAwait(false);

        trigger.IsDeleted = true;
        trigger.IsDisabled = true;

        return await SaveRevisionAsync(trigger, resetState: false, ct).ConfigureAwait(false);
    }

    private ProjectTrigger BuildTrigger(
        Project project, ProjectTriggerDefinition definition, CreateProjectTriggerCommand command)
    {
        return new ProjectTrigger
        {
            ProjectId = project.Id,
            SpaceId = project.SpaceId,
            Name = command.Name.Trim(),
            Description = command.Description,
            TriggerType = definition.TriggerType,
            ActionType = definition.ActionType,
            ConfigurationJson = definition.ConfigurationJson,
            IsDisabled = command.IsDisabled,
            Revision = 1,
            ExecutionUserId = UserId
        };
    }

    private void UpdateExecution(ProjectTrigger trigger, ProjectTriggerDefinition definition)
    {
        trigger.TriggerType = definition.TriggerType;
        trigger.ActionType = definition.ActionType;
        trigger.ConfigurationJson = definition.ConfigurationJson;
        trigger.ExecutionUserId = UserId;
    }

    private async Task<ProjectTrigger> LoadRevisionAsync(ProjectTriggerRevisionCommand command, CancellationToken ct)
    {
        var project = await ProjectAsync(command, Permission.ProjectEdit, ct).ConfigureAwait(false);

        return await LoadRevisionAsync(project, command.TriggerId, command.ExpectedRevision, ct).ConfigureAwait(false);
    }

    private async Task<ProjectTrigger> LoadRevisionAsync(
        Project project, int triggerId, int expectedRevision, CancellationToken ct)
    {
        if (expectedRevision < 1)
            throw new ValidationException("A positive expected revision is required.");

        var trigger = await _projectTriggerDataProvider.GetForEditAsync(project, triggerId, ct).ConfigureAwait(false);

        if (trigger.Revision != expectedRevision)
            throw new ProjectTriggerRevisionConflictException();

        return trigger;
    }

    private async Task EnsureExecutableAsync(ProjectTriggerDefinition definition, CancellationToken ct)
    {
        var resources = await ValidatedScopeAsync(definition, ct).ConfigureAwait(false);

        await _access.EnsureActionAsync(UserId, definition, resources, ct).ConfigureAwait(false);
    }

    private static ProjectTriggerDefinition Definition(Project project, ProjectTriggerWriteCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Trim().Length > 200)
            throw new ValidationException("A trigger name of at most 200 characters is required.");

        if (string.IsNullOrWhiteSpace(command.TriggerType) || command.TriggerType.Length > 64
            || string.IsNullOrWhiteSpace(command.ActionType) || command.ActionType.Length > 64)
            throw new ValidationException("Trigger and action types of at most 64 characters are required.");

        if (command.Configuration.ValueKind != JsonValueKind.Object)
            throw new ValidationException("A configuration object with schemaVersion 1 is required.");

        return new ProjectTriggerDefinition(
            project.Id, project.SpaceId, command.TriggerType, command.ActionType, command.Configuration.GetRawText());
    }

    private static bool SameConfiguration(string stored, JsonElement proposed)
    {
        try
        {
            using var document = JsonDocument.Parse(stored);

            return JsonElement.DeepEquals(document.RootElement, proposed);
        }
        catch (JsonException)
        {
            // Older jsonb payloads may exceed the current parser depth. A validated replacement repairs them.
            return false;
        }
    }

    private async Task<ProjectTriggerChangeDto> SaveRevisionAsync(
        ProjectTrigger trigger, bool resetState, CancellationToken ct)
    {
        trigger.Revision = checked(trigger.Revision + 1);

        if (resetState) trigger.ExecutionRevision = trigger.Revision;

        await _projectTriggerDataProvider.SaveRevisionAsync(trigger, resetState, ct).ConfigureAwait(false);

        return Change(trigger);
    }

    private static ProjectTriggerChangeDto Change(ProjectTrigger trigger)
    {
        return new ProjectTriggerChangeDto(
            trigger.Id, trigger.Revision, trigger.ExecutionUserId, trigger.IsDisabled, trigger.IsDeleted);
    }
}
