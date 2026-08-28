using System.Text.Json;
using Squid.Message.Enums.OctopusImport;

namespace Squid.Core.Services.OctopusImport.Octopus;

public interface IOctopusResourceGraphBuilder : IScopedDependency
{
    OctopusResourceGraph Build(OctopusManifestInventoryResult inventory);
}

public class OctopusResourceGraphBuilder : IOctopusResourceGraphBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public OctopusResourceGraph Build(OctopusManifestInventoryResult inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var diagnostics = new List<OctopusInputExtractionDiagnostic>(inventory.Diagnostics);
        var resources = new List<OctopusResourceNode>();
        var references = new List<OctopusResourceReference>();
        var resourceIndexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in inventory.Items.Where(i => i.HasDocument))
        {
            var context = new GraphBuildContext(item, diagnostics, resources, references, resourceIndexes);
            AddDocumentResource(context);
        }

        var dependencies = references
            .Where(r => r.CreatesDependency && !string.IsNullOrWhiteSpace(r.ToSourceId))
            .Select(r => new OctopusResourceDependency(r.FromSourceId, r.ToSourceId, r.ReferenceKind, r.ToKind))
            .Distinct()
            .ToList();

        return new OctopusResourceGraph(resources, references, dependencies, diagnostics);
    }

    private static void AddDocumentResource(GraphBuildContext context)
    {
        switch (context.Classification.Kind)
        {
            case OctopusDocumentKind.Project:
                AddProject(context);
                break;
            case OctopusDocumentKind.ProjectGroup:
                AddDocument<OctopusProjectGroupDto>(context, OctopusResourceKind.ProjectGroup);
                break;
            case OctopusDocumentKind.Environment:
                AddDocument<OctopusEnvironmentDto>(context, OctopusResourceKind.Environment);
                break;
            case OctopusDocumentKind.Lifecycle:
                AddLifecycle(context);
                break;
            case OctopusDocumentKind.Channel:
                AddChannel(context);
                break;
            case OctopusDocumentKind.DeploymentSettings:
                AddDeploymentSettings(context);
                break;
            case OctopusDocumentKind.DeploymentProcess:
            case OctopusDocumentKind.DeploymentProcessSnapshot:
                AddDeploymentProcess(context);
                break;
            case OctopusDocumentKind.VariableSet:
            case OctopusDocumentKind.VariableSetSnapshot:
                AddVariableSet(context);
                break;
            case OctopusDocumentKind.Feed:
                AddDocument<OctopusFeedDto>(context, OctopusResourceKind.Feed);
                break;
            case OctopusDocumentKind.Team:
                AddDocument<OctopusTeamDto>(context, OctopusResourceKind.Team);
                break;
            case OctopusDocumentKind.Machine:
                AddMachine(context);
                break;
            case OctopusDocumentKind.Account:
                AddAccount(context);
                break;
            case OctopusDocumentKind.Certificate:
                AddDocument<OctopusCertificateDto>(context, OctopusResourceKind.Certificate);
                break;
            case OctopusDocumentKind.Release:
                AddRelease(context);
                break;
            case OctopusDocumentKind.Deployment:
                AddDeployment(context);
                break;
            case OctopusDocumentKind.ServerTask:
                AddServerTask(context);
                break;
            case OctopusDocumentKind.ActionTemplate:
                AddJsonDocument(context, OctopusResourceKind.ActionTemplate);
                break;
            case OctopusDocumentKind.WorkerPool:
                AddJsonDocument(context, OctopusResourceKind.WorkerPool);
                break;
            default:
                AddJsonDocument(context, OctopusResourceKind.Unknown);
                break;
        }
    }

    private static void AddProject(GraphBuildContext context)
    {
        var project = Deserialize<OctopusProjectDto>(context);
        if (project == null)
            return;

        if (!AddDocumentNode(context, project, OctopusResourceKind.Project, project.Id, project.Name, project.Id))
            return;

        AddReference(context, project.Id, OctopusResourceKind.Project, OctopusResourceReferenceKind.ProjectGroup, project.ProjectGroupId, OctopusResourceKind.ProjectGroup, project.Id, true);
        AddReference(context, project.Id, OctopusResourceKind.Project, OctopusResourceReferenceKind.Lifecycle, project.LifecycleId, OctopusResourceKind.Lifecycle, project.Id, true);
        AddReference(context, project.Id, OctopusResourceKind.Project, OctopusResourceReferenceKind.VariableSet, project.VariableSetId, OctopusResourceKind.VariableSet, project.Id, true, false);
        AddReference(context, project.Id, OctopusResourceKind.Project, OctopusResourceReferenceKind.DeploymentProcess, project.DeploymentProcessId, OctopusResourceKind.DeploymentProcess, project.Id, true, false);
        AddReference(context, project.Id, OctopusResourceKind.Project, OctopusResourceReferenceKind.DeploymentSettings, project.DeploymentSettingsId, OctopusResourceKind.DeploymentSettings, project.Id, true, false);

        foreach (var variableSetId in project.IncludedLibraryVariableSetIds)
            AddReference(context, project.Id, OctopusResourceKind.Project, OctopusResourceReferenceKind.VariableSet, variableSetId, OctopusResourceKind.VariableSet, project.Id, true);
    }

    private static void AddLifecycle(GraphBuildContext context)
    {
        var lifecycle = Deserialize<OctopusLifecycleDto>(context);
        if (lifecycle == null)
            return;

        if (!AddDocumentNode(context, lifecycle, OctopusResourceKind.Lifecycle, lifecycle.Id, lifecycle.Name, null))
            return;

        foreach (var phase in lifecycle.Phases.Select((value, index) => (value, index)))
        {
            var sourceId = BuildChildSourceId(lifecycle.Id, "phase", phase.value.Id, phase.index);
            if (!AddNode(context, sourceId, phase.value.Name, OctopusResourceKind.LifecyclePhase, OctopusDocumentKind.Lifecycle, context.Document.SourcePath, null, lifecycle.Id, false, phase.value))
                continue;

            AddReference(context, lifecycle.Id, OctopusResourceKind.Lifecycle, OctopusResourceReferenceKind.LifecyclePhase, sourceId, OctopusResourceKind.LifecyclePhase, null, false);

            foreach (var environmentId in phase.value.AutomaticDeploymentTargets)
                AddReference(context, sourceId, OctopusResourceKind.LifecyclePhase, OctopusResourceReferenceKind.Environment, environmentId, OctopusResourceKind.Environment, null, true);

            foreach (var environmentId in phase.value.OptionalDeploymentTargets)
                AddReference(context, sourceId, OctopusResourceKind.LifecyclePhase, OctopusResourceReferenceKind.Environment, environmentId, OctopusResourceKind.Environment, null, false);
        }
    }

    private static void AddChannel(GraphBuildContext context)
    {
        var channel = Deserialize<OctopusChannelDto>(context);
        if (channel == null)
            return;

        if (!AddDocumentNode(context, channel, OctopusResourceKind.Channel, channel.Id, channel.Name, channel.ProjectId))
            return;

        AddReference(context, channel.Id, OctopusResourceKind.Channel, OctopusResourceReferenceKind.Project, channel.ProjectId, OctopusResourceKind.Project, channel.ProjectId, true);
        AddReference(context, channel.Id, OctopusResourceKind.Channel, OctopusResourceReferenceKind.Lifecycle, channel.LifecycleId, OctopusResourceKind.Lifecycle, channel.ProjectId, true);
    }

    private static void AddDeploymentSettings(GraphBuildContext context)
    {
        var settings = Deserialize<OctopusDeploymentSettingsDto>(context);
        if (settings == null)
            return;

        if (!AddDocumentNode(context, settings, OctopusResourceKind.DeploymentSettings, settings.Id, settings.Name, settings.ProjectId))
            return;

        AddReference(context, settings.Id, OctopusResourceKind.DeploymentSettings, OctopusResourceReferenceKind.Project, settings.ProjectId, OctopusResourceKind.Project, settings.ProjectId, true);
    }

    private static void AddDeploymentProcess(GraphBuildContext context)
    {
        var process = Deserialize<OctopusDeploymentProcessDto>(context);
        if (process == null)
            return;

        var kind = context.Classification.Kind == OctopusDocumentKind.DeploymentProcessSnapshot
            ? OctopusResourceKind.DeploymentProcessSnapshot
            : OctopusResourceKind.DeploymentProcess;

        if (!AddDocumentNode(context, process, kind, process.Id, null, process.OwnerId))
            return;

        AddReference(context, process.Id, kind, OctopusResourceReferenceKind.Project, process.OwnerId, OctopusResourceKind.Project, process.OwnerId, true);

        if (kind == OctopusResourceKind.DeploymentProcessSnapshot)
            return;

        foreach (var step in process.Steps.Select((value, index) => (value, index)))
            AddDeploymentStep(context, process, kind, step.value, step.index);
    }

    private static void AddDeploymentStep(GraphBuildContext context, OctopusDeploymentProcessDto process, OctopusResourceKind processKind, OctopusDeploymentStepDto step, int index)
    {
        var sourceId = BuildChildSourceId(process.Id, "step", step.Id, index);
        if (!AddNode(context, sourceId, step.Name, OctopusResourceKind.DeploymentStep, context.Classification.Kind, context.Document.SourcePath, process.OwnerId, process.Id, context.Classification.IsOutOfScopeHistory, step))
            return;

        AddReference(context, sourceId, OctopusResourceKind.DeploymentStep, ProcessReferenceKind(processKind), process.Id, processKind, process.OwnerId, true);

        foreach (var action in step.Actions.Select((value, actionIndex) => (value, actionIndex)))
            AddDeploymentAction(context, process, sourceId, action.value, action.actionIndex);
    }

    private static void AddDeploymentAction(GraphBuildContext context, OctopusDeploymentProcessDto process, string stepId, OctopusDeploymentActionDto action, int index)
    {
        var sourceId = BuildChildSourceId(stepId, "action", action.Id, index);
        if (!AddNode(context, sourceId, action.Name, OctopusResourceKind.DeploymentAction, context.Classification.Kind, context.Document.SourcePath, process.OwnerId, stepId, context.Classification.IsOutOfScopeHistory, action))
            return;

        AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.DeploymentAction, stepId, OctopusResourceKind.DeploymentStep, process.OwnerId, true);

        foreach (var environmentId in action.Environments)
            AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Environment, environmentId, OctopusResourceKind.Environment, process.OwnerId, false);

        foreach (var environmentId in action.ExcludedEnvironments)
            AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Environment, environmentId, OctopusResourceKind.Environment, process.OwnerId, false);

        foreach (var channelId in action.Channels)
            AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Channel, channelId, OctopusResourceKind.Channel, process.OwnerId, false);

        AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Feed, action.Container?.FeedId, OctopusResourceKind.Feed, process.OwnerId, true);

        foreach (var package in action.Packages)
            AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Feed, package.FeedId, OctopusResourceKind.Feed, process.OwnerId, true);

        foreach (var targetRole in SplitReferenceList(GetProperty(action.Properties, "Octopus.Action.TargetRoles")))
            AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.TargetRole, targetRole, null, process.OwnerId, false);

        foreach (var teamId in SplitReferenceList(GetProperty(action.Properties, "Octopus.Action.Manual.ResponsibleTeamIds")))
            AddReference(context, sourceId, OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Team, teamId, OctopusResourceKind.Team, process.OwnerId, false);
    }

    private static void AddVariableSet(GraphBuildContext context)
    {
        var variableSet = Deserialize<OctopusVariableSetDto>(context);
        if (variableSet == null)
            return;

        var kind = context.Classification.Kind == OctopusDocumentKind.VariableSetSnapshot
            ? OctopusResourceKind.VariableSetSnapshot
            : OctopusResourceKind.VariableSet;

        if (!AddDocumentNode(context, variableSet, kind, variableSet.Id, null, variableSet.OwnerId))
            return;

        AddReference(context, variableSet.Id, kind, OctopusResourceReferenceKind.Project, variableSet.OwnerId, OctopusResourceKind.Project, variableSet.OwnerId, true);

        if (kind == OctopusResourceKind.VariableSetSnapshot)
            return;

        foreach (var variable in variableSet.Variables.Select((value, index) => (value, index)))
            AddVariable(context, variableSet, kind, variable.value, variable.index);
    }

    private static void AddVariable(GraphBuildContext context, OctopusVariableSetDto variableSet, OctopusResourceKind variableSetKind, OctopusVariableDto variable, int index)
    {
        var sourceId = BuildChildSourceId(variableSet.Id, "variable", variable.Id, index);
        if (!AddNode(context, sourceId, variable.Name, OctopusResourceKind.Variable, context.Classification.Kind, context.Document.SourcePath, variableSet.OwnerId, variableSet.Id, context.Classification.IsOutOfScopeHistory, variable))
            return;

        AddReference(context, sourceId, OctopusResourceKind.Variable, VariableSetReferenceKind(variableSetKind), variableSet.Id, variableSetKind, variableSet.OwnerId, true);

        foreach (var scope in variable.Scope)
        {
            foreach (var scopedValue in scope.Value)
                AddVariableScopeReference(context, sourceId, variableSet.OwnerId, scope.Key, scopedValue);
        }
    }

    private static void AddMachine(GraphBuildContext context)
    {
        var machine = Deserialize<OctopusMachineDto>(context);
        if (machine == null)
            return;

        if (!AddDocumentNode(context, machine, OctopusResourceKind.Machine, machine.Id, machine.Name, null))
            return;

        foreach (var environmentId in machine.EnvironmentIds)
            AddReference(context, machine.Id, OctopusResourceKind.Machine, OctopusResourceReferenceKind.Environment, environmentId, OctopusResourceKind.Environment, null, false);

        foreach (var role in machine.Roles)
            AddReference(context, machine.Id, OctopusResourceKind.Machine, OctopusResourceReferenceKind.TargetRole, role, null, null, false);
    }

    private static void AddAccount(GraphBuildContext context)
    {
        var account = Deserialize<OctopusAccountDto>(context);
        if (account == null)
            return;

        if (!AddDocumentNode(context, account, OctopusResourceKind.Account, account.Id, account.Name, null))
            return;

        foreach (var environmentId in account.EnvironmentIds)
            AddReference(context, account.Id, OctopusResourceKind.Account, OctopusResourceReferenceKind.Environment, environmentId, OctopusResourceKind.Environment, null, false);
    }

    private static void AddRelease(GraphBuildContext context)
    {
        var release = Deserialize<OctopusReleaseDto>(context);
        if (release == null)
            return;

        if (!AddDocumentNode(context, release, OctopusResourceKind.Release, release.Id, release.Version ?? release.Name, release.ProjectId))
            return;

        AddReference(context, release.Id, OctopusResourceKind.Release, OctopusResourceReferenceKind.Project, release.ProjectId, OctopusResourceKind.Project, release.ProjectId, true);
        AddReference(context, release.Id, OctopusResourceKind.Release, OctopusResourceReferenceKind.Channel, release.ChannelId, OctopusResourceKind.Channel, release.ProjectId, true);
        AddReference(context, release.Id, OctopusResourceKind.Release, OctopusResourceReferenceKind.VariableSetSnapshot, release.ProjectVariableSetSnapshotId, OctopusResourceKind.VariableSetSnapshot, release.ProjectId, true);
        AddReference(context, release.Id, OctopusResourceKind.Release, OctopusResourceReferenceKind.DeploymentProcessSnapshot, release.ProjectDeploymentProcessSnapshotId, OctopusResourceKind.DeploymentProcessSnapshot, release.ProjectId, true);
    }

    private static void AddDeployment(GraphBuildContext context)
    {
        var deployment = Deserialize<OctopusDeploymentDto>(context);
        if (deployment == null)
            return;

        if (!AddDocumentNode(context, deployment, OctopusResourceKind.Deployment, deployment.Id, deployment.Name, deployment.ProjectId))
            return;

        AddReference(context, deployment.Id, OctopusResourceKind.Deployment, OctopusResourceReferenceKind.Project, deployment.ProjectId, OctopusResourceKind.Project, deployment.ProjectId, true);
        AddReference(context, deployment.Id, OctopusResourceKind.Deployment, OctopusResourceReferenceKind.Environment, deployment.EnvironmentId, OctopusResourceKind.Environment, deployment.ProjectId, true);
        AddReference(context, deployment.Id, OctopusResourceKind.Deployment, OctopusResourceReferenceKind.Release, deployment.ReleaseId, OctopusResourceKind.Release, deployment.ProjectId, true);
        AddReference(context, deployment.Id, OctopusResourceKind.Deployment, OctopusResourceReferenceKind.ServerTask, deployment.TaskId, OctopusResourceKind.ServerTask, deployment.ProjectId, false);
    }

    private static void AddServerTask(GraphBuildContext context)
    {
        var task = Deserialize<OctopusServerTaskDto>(context);
        if (task == null)
            return;

        if (!AddDocumentNode(context, task, OctopusResourceKind.ServerTask, task.Id, task.Name, task.ProjectId))
            return;

        AddReference(context, task.Id, OctopusResourceKind.ServerTask, OctopusResourceReferenceKind.Project, task.ProjectId, OctopusResourceKind.Project, task.ProjectId, false);
        AddReference(context, task.Id, OctopusResourceKind.ServerTask, OctopusResourceReferenceKind.Environment, task.EnvironmentId, OctopusResourceKind.Environment, task.ProjectId, false);
    }

    private static void AddJsonDocument(GraphBuildContext context, OctopusResourceKind kind)
    {
        var sourceId = context.Classification.SourceId ?? context.Item.ManifestEntry.Id;
        AddNode(context, sourceId, context.Item.ManifestEntry.Name, kind, context.Classification.Kind, context.Document.SourcePath, null, null, context.Classification.IsOutOfScopeHistory, context.Document.Root.Clone());
    }

    private static bool AddDocument<T>(GraphBuildContext context, OctopusResourceKind kind)
        where T : OctopusDocumentDto
    {
        var document = Deserialize<T>(context);
        return document != null && AddDocumentNode(context, document, kind, document.Id, document.Name, null);
    }

    private static bool AddDocumentNode(
        GraphBuildContext context,
        object source,
        OctopusResourceKind kind,
        string sourceId,
        string name,
        string ownerProjectId)
        => AddNode(context, sourceId, name, kind, context.Classification.Kind, context.Document.SourcePath, ownerProjectId, null, context.Classification.IsOutOfScopeHistory, source);

    private static bool AddNode(
        GraphBuildContext context,
        string sourceId,
        string name,
        OctopusResourceKind kind,
        OctopusDocumentKind documentKind,
        string sourcePath,
        string ownerProjectId,
        string parentSourceId,
        bool isHistorical,
        object source)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            context.Diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.GraphResourceMissingSourceId,
                $"Octopus import graph resource in '{sourcePath}' is missing a source id.",
                sourcePath,
                documentKind: documentKind));

            return false;
        }

        if (!context.ResourceIndexes.TryGetValue(sourceId, out var existingIndex))
        {
            context.ResourceIndexes[sourceId] = context.Resources.Count;
            context.Resources.Add(new OctopusResourceNode(sourceId, name, kind, documentKind, sourcePath, ownerProjectId, parentSourceId, isHistorical, source));
            return true;
        }

        var existing = context.Resources[existingIndex];

        if (existing.IsHistorical && !isHistorical)
        {
            context.Resources[existingIndex] = new OctopusResourceNode(sourceId, name, kind, documentKind, sourcePath, ownerProjectId, parentSourceId, isHistorical, source);
            return true;
        }

        if (existing.IsHistorical || isHistorical)
            return false;

        context.Diagnostics.Add(Blocker(
            OctopusInputExtractionDiagnosticCodes.GraphDuplicateSourceId,
            $"Octopus import graph contains duplicate source id '{sourceId}'.",
            sourcePath,
            sourceId,
            documentKind));

        return false;
    }

    private static T Deserialize<T>(GraphBuildContext context)
        where T : class
    {
        try
        {
            return context.Document.Root.Deserialize<T>(JsonOptions);
        }
        catch (JsonException ex)
        {
            context.Diagnostics.Add(Blocker(
                OctopusInputExtractionDiagnosticCodes.GraphDocumentMalformed,
                $"Octopus import document '{context.Document.SourcePath}' could not be deserialized for graph building ({ex.GetType().Name}).",
                context.Document.SourcePath,
                context.Classification.SourceId,
                context.Classification.Kind));

            return null;
        }
    }

    private static void AddVariableScopeReference(GraphBuildContext context, string variableId, string ownerProjectId, string scopeKey, string scopedValue)
    {
        var referenceKind = scopeKey?.Trim() switch
        {
            "Environment" => OctopusResourceReferenceKind.Environment,
            "Channel" => OctopusResourceReferenceKind.Channel,
            "Action" => OctopusResourceReferenceKind.DeploymentAction,
            "Machine" => OctopusResourceReferenceKind.Machine,
            "Role" => OctopusResourceReferenceKind.TargetRole,
            "TenantTag" => OctopusResourceReferenceKind.TenantTag,
            _ => (OctopusResourceReferenceKind?)null
        };

        if (referenceKind == null)
            return;

        var toKind = referenceKind.Value switch
        {
            OctopusResourceReferenceKind.Environment => OctopusResourceKind.Environment,
            OctopusResourceReferenceKind.Channel => OctopusResourceKind.Channel,
            OctopusResourceReferenceKind.DeploymentAction => OctopusResourceKind.DeploymentAction,
            OctopusResourceReferenceKind.Machine => OctopusResourceKind.Machine,
            _ => (OctopusResourceKind?)null
        };

        AddReference(context, variableId, OctopusResourceKind.Variable, referenceKind.Value, scopedValue, toKind, ownerProjectId, false);
    }

    private static void AddReference(
        GraphBuildContext context,
        string fromSourceId,
        OctopusResourceKind fromKind,
        OctopusResourceReferenceKind referenceKind,
        string toSourceId,
        OctopusResourceKind? toKind,
        string ownerProjectId,
        bool isRequired,
        bool? createsDependency = null)
    {
        if (string.IsNullOrWhiteSpace(fromSourceId) || string.IsNullOrWhiteSpace(toSourceId))
            return;

        var addsDependency = createsDependency ?? isRequired;

        context.References.Add(new OctopusResourceReference(
            fromSourceId,
            fromKind,
            referenceKind,
            toSourceId,
            toKind,
            ownerProjectId,
            isRequired,
            addsDependency));
    }

    private static OctopusResourceReferenceKind ProcessReferenceKind(OctopusResourceKind processKind)
        => processKind == OctopusResourceKind.DeploymentProcessSnapshot
            ? OctopusResourceReferenceKind.DeploymentProcessSnapshot
            : OctopusResourceReferenceKind.DeploymentProcess;

    private static OctopusResourceReferenceKind VariableSetReferenceKind(OctopusResourceKind variableSetKind)
        => variableSetKind == OctopusResourceKind.VariableSetSnapshot
            ? OctopusResourceReferenceKind.VariableSetSnapshot
            : OctopusResourceReferenceKind.VariableSet;

    private static string BuildChildSourceId(string parentSourceId, string childKind, string sourceId, int index)
        => string.IsNullOrWhiteSpace(sourceId)
            ? $"{parentSourceId}/{childKind}-{index + 1}"
            : sourceId;

    private static string GetProperty(Dictionary<string, string> properties, string key)
    {
        return properties != null && properties.TryGetValue(key, out var value)
            ? value
            : null;
    }

    private static IEnumerable<string> SplitReferenceList(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v));
    }

    private static OctopusInputExtractionDiagnostic Blocker(
        string code,
        string message,
        string sourcePath = null,
        string sourceId = null,
        OctopusDocumentKind? documentKind = null)
        => new(OctopusImportCompatibilitySeverity.Blocker, code, message, sourcePath, sourceId, documentKind);

    private sealed record GraphBuildContext(
        OctopusManifestInventoryItem Item,
        List<OctopusInputExtractionDiagnostic> Diagnostics,
        List<OctopusResourceNode> Resources,
        List<OctopusResourceReference> References,
        Dictionary<string, int> ResourceIndexes)
    {
        public OctopusExtractedJsonDocument Document => Item.Document;

        public OctopusDocumentClassification Classification => Item.Classification;
    }
}
