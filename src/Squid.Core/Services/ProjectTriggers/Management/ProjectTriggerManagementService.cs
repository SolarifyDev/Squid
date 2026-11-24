using System.Text.Json;
using FluentValidation;
using Squid.Core.Persistence.Entities.Deployments;
using Squid.Core.Services.Identity;
using Squid.Message.Commands.ProjectTriggers;
using Squid.Message.Enums;
using Squid.Message.Models.ProjectTriggers;
using Squid.Message.Requests.ProjectTriggers;

namespace Squid.Core.Services.ProjectTriggers.Management;

public interface IProjectTriggerManagementService : IScopedDependency
{
    Task<ProjectTriggerPage<ProjectTriggerSummaryDto>> ListAsync(GetProjectTriggersRequest request, CancellationToken ct);

    Task<ProjectTriggerDto> DetailAsync(GetProjectTriggerRequest request, CancellationToken ct);

    Task<IReadOnlyList<ProjectTriggerCapability>> TypesAsync(GetProjectTriggerTypesRequest request, CancellationToken ct);

    Task<ProjectTriggerPage<ProjectTriggerRunDto>> HistoryAsync(GetProjectTriggerHistoryRequest request, CancellationToken ct);

    Task<ProjectTriggerChangeDto> CreateAsync(CreateProjectTriggerCommand command, CancellationToken ct);

    Task<ProjectTriggerChangeDto> UpdateAsync(UpdateProjectTriggerCommand command, CancellationToken ct);

    Task<ProjectTriggerChangeDto> EnableAsync(EnableProjectTriggerCommand command, CancellationToken ct);

    Task<ProjectTriggerChangeDto> DisableAsync(DisableProjectTriggerCommand command, CancellationToken ct);

    Task<ProjectTriggerChangeDto> DeleteAsync(DeleteProjectTriggerCommand command, CancellationToken ct);
}

public sealed partial class ProjectTriggerManagementService : IProjectTriggerManagementService
{
    private readonly IProjectTriggerDataProvider _projectTriggerDataProvider;
    private readonly IProjectTriggerAccess _access;
    private readonly IProjectTriggerRegistry _registry;
    private readonly ICurrentUser _currentUser;

    public ProjectTriggerManagementService(
        IProjectTriggerDataProvider data,
        IProjectTriggerAccess access,
        IProjectTriggerRegistry registry,
        ICurrentUser currentUser)
    {
        _projectTriggerDataProvider = data;
        _access = access;
        _registry = registry;
        _currentUser = currentUser;
    }

    private int UserId => _currentUser.IsInternal ? 0 : _currentUser.Id.GetValueOrDefault();

    public async Task<ProjectTriggerPage<ProjectTriggerSummaryDto>> ListAsync(
        GetProjectTriggersRequest request, CancellationToken ct)
    {
        var project = await ProjectAsync(request, Permission.ProjectView, ct).ConfigureAwait(false);
        var page = await _projectTriggerDataProvider.ListAsync(project, PageOffset(request), request.PageSize, ct)
            .ConfigureAwait(false);

        foreach (var item in page.Items)
            item.IsAvailable = IsAvailable(item.TriggerType, item.ActionType);

        return page;
    }

    public async Task<ProjectTriggerDto> DetailAsync(GetProjectTriggerRequest request, CancellationToken ct)
    {
        var project = await ProjectAsync(request, Permission.ProjectView, ct).ConfigureAwait(false);
        var (model, config) = await _projectTriggerDataProvider.DetailAsync(project, request.TriggerId, ct)
            .ConfigureAwait(false);

        model.IsAvailable = IsAvailable(model.TriggerType, model.ActionType);

        if (!model.IsAvailable) return model;

        var definition = new ProjectTriggerDefinition(
            project.Id, project.SpaceId, model.TriggerType, model.ActionType, config);

        await ReadConfigurationAsync(model, definition, ct).ConfigureAwait(false);

        return model;
    }

    public async Task<IReadOnlyList<ProjectTriggerCapability>> TypesAsync(
        GetProjectTriggerTypesRequest request, CancellationToken ct)
    {
        await ProjectAsync(request, Permission.ProjectView, ct).ConfigureAwait(false);

        return _registry.Capabilities;
    }

    public async Task<ProjectTriggerPage<ProjectTriggerRunDto>> HistoryAsync(
        GetProjectTriggerHistoryRequest request, CancellationToken ct)
    {
        var project = await ProjectAsync(request, Permission.ProjectView, ct).ConfigureAwait(false);

        return await _projectTriggerDataProvider.HistoryAsync(
            project, request.TriggerId, PageOffset(request), request.PageSize, ct).ConfigureAwait(false);
    }

    private async Task ReadConfigurationAsync(
        ProjectTriggerDto model, ProjectTriggerDefinition definition, CancellationToken ct)
    {
        try
        {
            // Older payloads may predate this handler. Only expose configuration the installed handler accepts.
            await ValidatedScopeAsync(definition, ct).ConfigureAwait(false);
            model.Configuration = JsonSerializer.Deserialize<JsonElement>(definition.ConfigurationJson);
        }
        catch (ValidationException)
        {
            model.Configuration = null;
        }
    }

    private static int PageOffset(ProjectTriggerPageRequest request)
    {
        if (request.PageIndex < 1 || request.PageSize is < 1 or > 100)
            throw new ValidationException("PageIndex must be positive and PageSize must be between 1 and 100.");

        var skip = (long)(request.PageIndex - 1) * request.PageSize;

        if (skip > int.MaxValue)
            throw new ValidationException("The requested page offset is too large.");

        return (int)skip;
    }

    private Task<Project> ProjectAsync(
        ProjectTriggerProjectScope request, Permission permission, CancellationToken ct)
    {
        return _access.EnsureProjectAsync(UserId, request.ProjectId, request.SpaceId, permission, ct);
    }

    private bool IsAvailable(string triggerType, string actionType)
    {
        return _registry.Capabilities.Contains(new ProjectTriggerCapability(triggerType, actionType));
    }

    private async Task<TriggerResourceScope> ValidatedScopeAsync(
        ProjectTriggerDefinition definition, CancellationToken ct)
    {
        if (!IsAvailable(definition.TriggerType, definition.ActionType))
            throw new ValidationException("This trigger type and action are not available.");

        ValidateConfiguration(definition.ConfigurationJson);

        return await ValidateHandlerScopeAsync(definition, ct).ConfigureAwait(false);
    }

    private static void ValidateConfiguration(string configurationJson)
    {
        try
        {
            ProjectTriggerConfiguration.Validate(configurationJson);
        }
        catch (ArgumentException ex)
        {
            // The common schema guard emits fixed public messages, including the expected schema version.
            throw new ValidationException(ex.Message);
        }
    }

    private async Task<TriggerResourceScope> ValidateHandlerScopeAsync(
        ProjectTriggerDefinition definition, CancellationToken ct)
    {
        try
        {
            var handler = _registry.Resolve(definition.TriggerType, definition.ActionType);

            return await handler.ValidateAsync(definition, ct).ConfigureAwait(false)
                ?? throw new ValidationException("No resource scope was provided.");
        }
        catch (Exception ex) when (ex is ArgumentException or ValidationException or JsonException
            or KeyNotFoundException or FormatException)
        {
            // Handler errors may contain input or backend details. Do not expose either in responses or logs.
            throw new ValidationException("The configuration is invalid for the selected trigger and action.");
        }
    }
}
