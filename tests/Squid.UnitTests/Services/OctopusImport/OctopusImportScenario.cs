using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.Deployments.ExternalFeeds;
using Squid.Core.Services.Deployments.Variables;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
using Squid.Message.Enums;
using Squid.Message.Models.Deployments.ExternalFeed;
using Squid.Message.Models.Deployments.Variable;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

/// <summary>
/// Builds a realistic Octopus export (JSON documents + manifest) and runs it through the REAL
/// extraction → inventory → graph → dependency planner → conflict discovery → preview planner →
/// preview validator chain. Only the destination database is faked (a list of existing resources),
/// so preview/validation scenario tests exercise the same seams production does.
/// </summary>
internal sealed class OctopusImportScenario
{
    private readonly List<(string Id, string DocumentType, string Json)> _documents = [];
    private readonly List<OctopusImportDestinationResource> _destination = [];
    private readonly Dictionary<(string Type, string Id), OctopusImportPriorMapping> _prior = [];
    private readonly List<OctopusImportReleaseKey> _releaseKeys = [];
    private readonly List<string> _priorExportSourceIds = [];
    private readonly List<SensitiveVariableSlot> _squidVariables = [];
    private readonly List<ExternalFeedCredentialSlot> _squidFeeds = [];
    private readonly Dictionary<int, string> _storedReleaseNotes = [];
    private readonly Dictionary<int, OctopusImportStoredTask> _storedTasks = [];
    private readonly HashSet<int> _orphanedReleases = [];
    private readonly HashSet<int> _orphanedDeployments = [];
    private readonly HashSet<(string Type, string Id)> _deletedInSquid = [];
    private readonly List<OctopusImportDestinationChild> _squidChildren = [];
    private string _installationId;
    private string _priorInstallationId;
    private OctopusImportHistoryFetchDto _priorImportSession;
    private OctopusImportHistoryFetchDto _sourceImportSession;
    private readonly List<OctopusImportHistoryFetchDto> _otherImportSessions = [];
    private string _exportSpaceId;
    private bool _rowsOfUnknownOrigin;

    public OctopusImportScenario Environment(string id, string name)
        => Add(id, "StaticDeploymentEnvironment", new { Id = id, Name = name, Slug = name.ToLowerInvariant() });

    public OctopusImportScenario Machine(string id, string name, params string[] roles)
        => Add(id, "Machine", new { Id = id, Name = name, Roles = roles, EnvironmentIds = Array.Empty<string>() });

    public OctopusImportScenario Channel(string id, string projectId, string name)
        => Add(id, "Channel", new { Id = id, Name = name, ProjectId = projectId, IsDefault = false, Slug = name.ToLowerInvariant() });

    public OctopusImportScenario Feed(string id, string name, string feedType)
        => Add(id, "Feed", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), FeedType = feedType, FeedUri = "https://packages.example.com" });

    /// <summary>A Docker feed as an export file carries it: username and the password encrypted with the export password (null = none).</summary>
    public OctopusImportScenario FeedWithCredentials(string id, string name, string username, string encryptedPassword)
        => Add(id, "Feed", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), FeedType = "Docker", FeedUri = "https://registry.example.com", Username = username, Password = encryptedPassword });

    public OctopusImportScenario Account(string id, string name, string accountType, params string[] environmentIds)
        => Add(id, "Account", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), AccountType = accountType, EnvironmentIds = environmentIds });

    public OctopusImportScenario Lifecycle(string id, string name, params string[] environmentIds)
        => Add(id, "Lifecycle", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), Phases = new[] { new { Id = $"{id}-phase", Name = "Phase", AutomaticDeploymentTargets = environmentIds, OptionalDeploymentTargets = Array.Empty<string>() } } });

    /// <summary>A lifecycle without phases: Octopus deploys it to every environment.</summary>
    public OctopusImportScenario PhaselessLifecycle(string id, string name)
        => Add(id, "Lifecycle", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), Phases = Array.Empty<object>() });

    /// <summary>A lifecycle whose phases (named by their ids) each deploy to <paramref name="environmentId"/>, in this order.</summary>
    public OctopusImportScenario LifecycleWithPhases(string id, string name, string environmentId, params string[] phaseIds)
        => Add(id, "Lifecycle", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), Phases = phaseIds.Select(phaseId => new { Id = phaseId, Name = phaseId, AutomaticDeploymentTargets = new[] { environmentId }, OptionalDeploymentTargets = Array.Empty<string>() }).ToArray() });

    /// <summary>A project whose process is one step ("Step 1", id <c>{id}-step-1</c>) holding every action, in order.</summary>
    public OctopusImportScenario ProjectWithOneStep(string id, string name, params object[] actions)
    {
        Add(id, "Project", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), VariableSetId = ProjectVariableSetId(id), DeploymentProcessId = $"deploymentprocess-{id}", IncludedLibraryVariableSetIds = Array.Empty<string>() });
        Add(ProjectVariableSetId(id), "ProjectVariables", new { Id = ProjectVariableSetId(id), OwnerId = id, OwnerType = "Project", Variables = Array.Empty<object>() });

        var step = new { Id = $"{id}-step-1", Name = "Step 1", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(), Actions = actions };

        return Add($"deploymentprocess-{id}", "DeploymentProcess", new { Id = $"deploymentprocess-{id}", OwnerId = id, Steps = new[] { step } });
    }

    /// <summary>A project with no variables and no steps that deploys through <paramref name="lifecycleId"/>.</summary>
    public OctopusImportScenario ProjectOnLifecycle(string id, string name, string lifecycleId)
    {
        Add(id, "Project", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), LifecycleId = lifecycleId, VariableSetId = ProjectVariableSetId(id), DeploymentProcessId = $"deploymentprocess-{id}", IncludedLibraryVariableSetIds = Array.Empty<string>() });
        Add(ProjectVariableSetId(id), "ProjectVariables", new { Id = ProjectVariableSetId(id), OwnerId = id, OwnerType = "Project", Variables = Array.Empty<object>() });

        return Add($"deploymentprocess-{id}", "DeploymentProcess", new { Id = $"deploymentprocess-{id}", OwnerId = id, Steps = Array.Empty<object>() });
    }

    public OctopusImportScenario Team(string id, string name)
        => Add(id, "ConfigurableTeam", new { Id = id, Name = name, MemberUserIds = Array.Empty<string>(), ExternalSecurityGroups = Array.Empty<string>() });

    public OctopusImportScenario Library(string id, string name, params object[] variables)
        => LibraryDocument(id, name).LibraryVariables(id, variables);

    public OctopusImportScenario LibraryDocument(string id, string name)
        => Add(id, "LibraryVariableSet", new { Id = id, Name = name, VariableSetId = LibraryVariableSetId(id), ContentType = "Variables" });

    public OctopusImportScenario LibraryVariables(string libraryId, params object[] variables)
        => Add(LibraryVariableSetId(libraryId), "LibraryVariables", new { Id = LibraryVariableSetId(libraryId), OwnerId = libraryId, OwnerType = "LibraryVariableSet", Variables = variables });

    public OctopusImportScenario Project(string id, string name, string[] libraryIds, object[] variables, params object[] actions)
    {
        Add(id, "Project", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), VariableSetId = ProjectVariableSetId(id), DeploymentProcessId = $"deploymentprocess-{id}", IncludedLibraryVariableSetIds = libraryIds });
        Add(ProjectVariableSetId(id), "ProjectVariables", new { Id = ProjectVariableSetId(id), OwnerId = id, OwnerType = "Project", Variables = variables });

        var steps = actions.Select((action, index) => new { Id = $"{id}-step-{index + 1}", Name = $"Step {index + 1}", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(), Actions = new[] { action } });

        return Add($"deploymentprocess-{id}", "DeploymentProcess", new { Id = $"deploymentprocess-{id}", OwnerId = id, Steps = steps });
    }

    /// <summary>
    /// An Octopus Config-as-Code project as an export or the API carries it: the project document names a process and settings that
    /// live in git, so no process document is present. Its variables (still in Octopus) and a channel are.
    /// </summary>
    public OctopusImportScenario ConfigAsCodeProject(string id, string name, string lifecycleId, string[] libraryIds, params object[] variables)
    {
        Add(id, "Project", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), LifecycleId = lifecycleId, VariableSetId = ProjectVariableSetId(id), DeploymentProcessId = $"deploymentprocess-{id}", DeploymentSettingsId = $"deploymentsettings-{id}", IncludedLibraryVariableSetIds = libraryIds, IsVersionControlled = true, PersistenceSettings = new { Type = "VersionControlled" } });
        Add(ProjectVariableSetId(id), "ProjectVariables", new { Id = ProjectVariableSetId(id), OwnerId = id, OwnerType = "Project", Variables = variables });

        return Channel($"{id}-channel", id, "Default");
    }

    // ── REST-shaped resources: written through the REAL normaliser, exactly as the server source stages them ──

    /// <summary>A Docker feed as the Octopus API returns it; with <paramref name="hasPassword"/> its password is withheld.</summary>
    public OctopusImportScenario RestFeed(string id, string name, string username, bool hasPassword)
        => AddRest(OctopusResourceKind.Feed, "Feed", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), FeedType = "Docker", FeedUri = "https://registry.example.com", Username = username, Password = new { HasValue = hasPassword, NewValue = (string)null, Hint = (string)null } });

    /// <summary>A token account as the Octopus API returns it (secret withheld).</summary>
    public OctopusImportScenario RestTokenAccount(string id, string name)
        => AddRest(OctopusResourceKind.Account, "Account", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), AccountType = "Token", EnvironmentIds = Array.Empty<string>(), Token = new { HasValue = true, NewValue = (string)null, Hint = (string)null } });

    public OctopusImportScenario RestLibrary(string id, string name, params object[] variables)
    {
        Add(id, "LibraryVariableSet", new { Id = id, Name = name, VariableSetId = LibraryVariableSetId(id), ContentType = "Variables" });

        return AddRest(OctopusResourceKind.VariableSet, "LibraryVariables", new { Id = LibraryVariableSetId(id), OwnerId = id, Variables = variables });
    }

    /// <summary>A project whose variable set and process come from the Octopus API (no OwnerType, ProjectId instead of OwnerId).</summary>
    public OctopusImportScenario RestProject(string id, string name, string[] libraryIds, object[] variables, params object[] actions)
    {
        Add(id, "Project", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), VariableSetId = ProjectVariableSetId(id), DeploymentProcessId = $"deploymentprocess-{id}", IncludedLibraryVariableSetIds = libraryIds });
        AddRest(OctopusResourceKind.VariableSet, "ProjectVariables", new { Id = ProjectVariableSetId(id), OwnerId = id, Variables = variables });

        var steps = actions.Select((action, index) => new { Id = $"{id}-step-{index + 1}", Name = $"Step {index + 1}", Condition = "Success", StartTrigger = "StartAfterPrevious", PackageRequirement = "LetOctopusDecide", Properties = new Dictionary<string, string>(), Actions = new[] { action } });

        return AddRest(OctopusResourceKind.DeploymentProcess, "DeploymentProcess", new { Id = $"deploymentprocess-{id}", ProjectId = id, Steps = steps });
    }

    /// <summary>A sensitive variable as the Octopus API returns it: no value, IsSensitive and Type Sensitive.</summary>
    public static object RestSecret(string id, string name, Dictionary<string, string[]> scope = null)
        => new { Id = id, Name = name, Value = (string)null, Type = "Sensitive", IsSensitive = true, IsEditable = true, Prompt = (object)null, Scope = scope ?? new Dictionary<string, string[]>() };

    /// <summary>A script action with one sensitive property whose value the Octopus API withholds.</summary>
    public static object RestScriptActionWithSecret(string id, string secretPropertyName)
        => new { Id = id, Name = id, ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, object> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = "echo hi", ["Octopus.Action.RunOnServer"] = "true", [secretPropertyName] = new { HasValue = true, NewValue = (string)null } } };

    private OctopusImportScenario AddRest(OctopusResourceKind kind, string documentType, object restDocument)
    {
        var normalized = Squid.Core.Services.OctopusImport.Rest.OctopusRestDocumentNormalizer.Normalize(kind, JsonSerializer.SerializeToNode(restDocument)!.AsObject());

        _documents.Add((normalized.Id, documentType, normalized.Document.ToJsonString()));
        return this;
    }

    public OctopusImportScenario ExistingLibrary(int id, string name, DateTimeOffset? lastModified = null)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, OctopusResourceKind.LibraryVariableSet, name, null, lastModified ?? DateTimeOffset.UtcNow.AddDays(-1)));
        return this;
    }

    public OctopusImportScenario ExistingEnvironment(int id, string name)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, OctopusResourceKind.Environment, name, name.ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(-1)));
        return this;
    }

    public OctopusImportScenario Existing(OctopusResourceKind kind, int id, string name)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, kind, name, name.ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(-1)));
        return this;
    }

    /// <summary>A Squid channel of a destination project (one the user made by hand, or one an import made).</summary>
    public OctopusImportScenario ExistingChannel(int id, int projectId, string name)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, OctopusResourceKind.Channel, name, name.ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(-1)) { ProjectId = projectId });
        return this;
    }

    public OctopusImportScenario ExistingProject(int id, string name)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, OctopusResourceKind.Project, name, name.ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(-1)));
        return this;
    }

    public OctopusImportScenario Release(string id, string projectId, string channelId, string version, string notes = null)
        => Add(id, "Release", new { Id = id, ProjectId = projectId, ChannelId = channelId, Version = version, ReleaseNotes = notes, SelectedPackages = Array.Empty<object>() });

    /// <summary>A release whose one selected package names its feed directly (the shape the REST source writes).</summary>
    public OctopusImportScenario ReleaseWithPackage(string id, string projectId, string channelId, string version, string actionName, string feedId)
        => Add(id, "Release", new { Id = id, ProjectId = projectId, ChannelId = channelId, Version = version, SelectedPackages = new[] { new { ActionName = actionName, Version = "1.0.0", FeedId = feedId } } });

    public OctopusImportScenario Deployment(string id, string projectId, string channelId, string environmentId, string releaseId, string taskId)
        => Add(id, "Deployment", new { Id = id, Name = $"Deploy {id}", ProjectId = projectId, ChannelId = channelId, EnvironmentId = environmentId, ReleaseId = releaseId, TaskId = taskId, DeployedToMachineIds = Array.Empty<string>() });

    public OctopusImportScenario ServerTask(string id, string state = "Success", string detailFingerprint = null)
        => Add(id, "ServerTask", TaskDocument(id, state, detailFingerprint));

    public OctopusImportScenario ServerTaskDocument(OctopusServerTaskDto task)
        => Add(task.Id, "ServerTask", task);

    /// <summary>A source-map row an earlier confirmation recorded.</summary>
    public OctopusImportScenario ExistingMapping(OctopusResourceKind kind, string sourceId, int destinationId, string fingerprint = null, string detailFingerprint = null)
    {
        _prior[(kind.ToString(), sourceId)] = new OctopusImportPriorMapping(destinationId, "Created", fingerprint, detailFingerprint);
        return this;
    }

    /// <summary>A source-map row an earlier import of the named origin created (installation id and / or export fingerprint).</summary>
    public OctopusImportScenario ExistingMappingCreatedBy(OctopusResourceKind kind, string sourceId, int destinationId, string createdInstallationId, string createdExportSourceId = null)
    {
        _prior[(kind.ToString(), sourceId)] = new OctopusImportPriorMapping(destinationId, "Created", null, null, createdInstallationId, createdExportSourceId);
        return this;
    }

    public OctopusImportScenario WithoutMapping(OctopusResourceKind kind, string sourceId)
    {
        _prior.Remove((kind.ToString(), sourceId));
        return this;
    }

    /// <summary>Imported history whose owner a Squid delete removed: the release or deployment row is still there, pointing at the deleted row.</summary>
    public OctopusImportScenario OrphanedInSquid(OctopusResourceKind kind, string sourceId)
    {
        var destinationId = PriorDestinationOf(kind, sourceId);

        (kind == OctopusResourceKind.Release ? _orphanedReleases : _orphanedDeployments).Add(destinationId);
        return this;
    }

    /// <summary>An imported child whose Squid row was deleted since: the source map still records it, its destination is gone.</summary>
    public OctopusImportScenario DeletedInSquid(OctopusResourceKind kind, string sourceId)
    {
        _prior.Remove((kind.ToString(), sourceId));
        _deletedInSquid.Add((kind.ToString(), sourceId));
        return this;
    }

    /// <summary>A row Squid holds under an owner now, by name (a step of a process, an action of a step, a phase of a lifecycle).</summary>
    public OctopusImportScenario SquidChild(OctopusResourceKind kind, int ownerId, string name)
    {
        _squidChildren.Add(new OctopusImportDestinationChild(kind.ToString(), ownerId, name));
        return this;
    }

    /// <summary>A variable Squid holds in a set now: its row id, name and scope (compared as VariableIdentity tokens).</summary>
    public OctopusImportScenario SquidVariable(int id, int variableSetId, string name, params (VariableScopeType Type, string Value)[] scopes)
    {
        _squidChildren.Add(new OctopusImportDestinationChild(nameof(OctopusResourceKind.Variable), variableSetId, name) { Id = id, Scopes = VariableIdentity.Of(name, VariableType.String, scopes).Scopes });
        return this;
    }

    /// <summary>A release that exists in Squid under a destination project and channel.</summary>
    public OctopusImportScenario ExistingRelease(int projectId, int channelId, string version)
    {
        _releaseKeys.Add(new OctopusImportReleaseKey(projectId, channelId, version));
        return this;
    }

    /// <summary>The Octopus installation the incoming source (manifest) came from.</summary>
    public OctopusImportScenario Installation(string installationId)
    {
        _installationId = installationId;
        return this;
    }

    /// <summary>A real export's suffixed space id: every document carries it, so the graph has an export fingerprint (a re-importable source).</summary>
    public const string SuffixedExportSpaceId = "Spaces-1-00112233445566778899AABBCCDDEEFF";

    /// <summary>
    /// Source-map rows added without a creating origin stay of unknown origin (recorded before origins were kept per row). Otherwise
    /// they are created by the space's recorded origin, or — with none recorded — by the incoming source itself (its earlier import).
    /// </summary>
    public OctopusImportScenario RowsOfUnknownOrigin()
    {
        _rowsOfUnknownOrigin = true;
        return this;
    }

    /// <summary>The Octopus installation an earlier import of this space recorded.</summary>
    public OctopusImportScenario PriorInstallation(string installationId)
    {
        _priorInstallationId = installationId;
        return this;
    }

    /// <summary>Every document carries this <c>SpaceId</c>, as an Octopus export does (a suffixed one is the export's fingerprint).</summary>
    public OctopusImportScenario ExportedFromSpace(string spaceId)
    {
        _exportSpaceId = spaceId;
        return this;
    }

    /// <summary>An export fingerprint an earlier export-file import recorded for the space (the first one is pinned).</summary>
    public OctopusImportScenario PriorExportSource(string exportSourceId)
    {
        _priorExportSourceIds.Add(exportSourceId);
        return this;
    }

    /// <summary>A source-map row with an explicit outcome (e.g. a <c>VariableSecret</c> / <c>FeedSecret</c> row an API import left <c>Missing</c>).</summary>
    public OctopusImportScenario PriorRow(string sourceType, string sourceId, int destinationId, string outcome)
    {
        _prior[(sourceType, sourceId)] = new OctopusImportPriorMapping(destinationId, outcome, null, null);
        return this;
    }

    /// <summary>The <c>VariableSecret</c> row key of a sensitive variable, as the import that left it missing recorded it (scopes through the prior rows).</summary>
    public string VariableSecretKey(string ownerSetSourceId, string name, Dictionary<string, string[]> scope = null)
        => OctopusImportSecretKeys.ForVariable(ownerSetSourceId, new OctopusVariableDto { Name = name, Type = "Sensitive", IsSensitive = true, Scope = (scope ?? []).ToDictionary(kv => kv.Key, kv => kv.Value.ToList()) }, (id, kind) => _prior.TryGetValue((kind.ToString(), id), out var row) ? row.DestinationId : null);

    /// <summary>A sensitive variable as Squid holds it now (the value itself is never read, only whether there is one).</summary>
    public OctopusImportScenario SquidSensitiveVariable(int variableId, int variableSetId, string name, bool hasValue, params (VariableScopeType Type, string Value)[] scopes)
    {
        _squidVariables.Add(new SensitiveVariableSlot(variableId, variableSetId, VariableIdentity.Of(name, VariableType.Password, scopes), hasValue));
        return this;
    }

    /// <summary>A Squid project (or library variable set) that owns the given variable set, as the destination data provider lists it.</summary>
    public OctopusImportScenario ExistingVariableSetOwner(OctopusResourceKind kind, int id, string name, int variableSetId)
    {
        _destination.Add(new OctopusImportDestinationResource(id, SpaceId, kind, name, name.ToLowerInvariant(), DateTimeOffset.UtcNow.AddDays(-1)) { VariableSetId = variableSetId });
        return this;
    }

    /// <summary>The import session that recorded the space's installation: the server, space and version it read.</summary>
    public OctopusImportScenario PriorImportSession(string serverHost, string octopusSpaceId, string octopusVersion)
    {
        _priorImportSession = new OctopusImportHistoryFetchDto { InstallationId = _priorInstallationId, ServerHost = serverHost, OctopusSpaceId = octopusSpaceId, OctopusVersion = octopusVersion };
        return this;
    }

    /// <summary>The import session reading the incoming source from an Octopus server: the server, space and version it read.</summary>
    public OctopusImportScenario SourceImportSession(string serverHost, string octopusSpaceId, string octopusVersion)
    {
        _sourceImportSession = new OctopusImportHistoryFetchDto { InstallationId = _installationId, ServerHost = serverHost, OctopusSpaceId = octopusSpaceId, OctopusVersion = octopusVersion };
        return this;
    }

    /// <summary>An import session of another installation into the space (e.g. the one that created rows of a third origin).</summary>
    public OctopusImportScenario ImportSession(string installationId, string serverHost, string octopusSpaceId, string octopusVersion)
    {
        _otherImportSessions.Add(new OctopusImportHistoryFetchDto { InstallationId = installationId, ServerHost = serverHost, OctopusSpaceId = octopusSpaceId, OctopusVersion = octopusVersion });
        return this;
    }

    public OctopusImportScenario SquidFeed(int feedId, string username, bool hasPassword)
    {
        _squidFeeds.Add(new ExternalFeedCredentialSlot(feedId, username, hasPassword));
        return this;
    }

    /// <summary>
    /// Records every resource a plan writes as imported, the way confirmation fills the source map, and what it wrote into Squid: the
    /// project rows (by name) and the release notes / task scalars an Update would later compare with.
    /// </summary>
    public OctopusImportScenario ImportedAs(ScenarioResult plan)
    {
        var nodes = plan.Graph.Resources.ToDictionary(n => n.SourceId, StringComparer.OrdinalIgnoreCase);
        var destinationId = 1000;

        foreach (var resource in plan.Preview.Resources.Where(r => r.PreviewAction == Squid.Message.Enums.OctopusImport.OctopusImportPreviewAction.Create))
        {
            var node = nodes[resource.SourceId];

            ExistingMapping(node.Kind, resource.SourceId, ++destinationId, FingerprintOf(node, nodes));
            RecordWrittenRow(node, destinationId);
        }

        return this;
    }

    /// <summary>The Squid destination id an earlier import (or <see cref="ImportedAs"/>) recorded for a source resource.</summary>
    public int PriorDestinationOf(OctopusResourceKind kind, string sourceId) => _prior[(kind.ToString(), sourceId)].DestinationId;

    /// <summary>Squid's current notes of an imported release.</summary>
    public OctopusImportScenario StoredReleaseNotes(int releaseId, string notes)
    {
        _storedReleaseNotes[releaseId] = notes;
        return this;
    }

    /// <summary>Squid's current scalars of an imported task.</summary>
    public OctopusImportScenario StoredTask(OctopusImportStoredTask task)
    {
        _storedTasks[task.Id] = task;
        return this;
    }

    /// <summary>The owners conflict discovery asked Squid for the child names of, per kind.</summary>
    public List<(OctopusResourceKind Kind, IReadOnlyCollection<int> OwnerIds)> ChildNameReads { get; } = [];

    /// <summary>The release / task ids conflict discovery asked Squid for (the rows an Update would rewrite).</summary>
    public List<(IReadOnlyCollection<int> ReleaseIds, IReadOnlyCollection<int> TaskIds)> StoredHistoryReads { get; } = [];

    private void RecordWrittenRow(OctopusResourceNode node, int destinationId)
    {
        switch (node.Kind)
        {
            case OctopusResourceKind.Project:
                ExistingProject(destinationId, node.Name);
                break;
            case OctopusResourceKind.Release:
                _storedReleaseNotes[destinationId] = node.GetSource<OctopusReleaseDto>()?.ReleaseNotes;
                break;
            case OctopusResourceKind.ServerTask:
                _storedTasks[destinationId] = StoredTaskOf(destinationId, node.GetSource<OctopusServerTaskDto>());
                break;
        }
    }

    // What the history importer writes: the state normalised, times in UTC.
    private static OctopusImportStoredTask StoredTaskOf(int id, OctopusServerTaskDto task)
        => new(id, Squid.Core.Services.OctopusImport.History.OctopusImportTaskStateNormalizer.Normalize(task.State).State, (task.QueueTime ?? DateTimeOffset.UtcNow).ToUniversalTime(), task.StartTime?.ToUniversalTime(), task.CompletedTime?.ToUniversalTime(), task.ErrorMessage ?? string.Empty, task.HasWarningsOrErrors);

    public static OctopusServerTaskDto TaskDocument(string id, string state = "Success", string detailFingerprint = null)
        => new() { Id = id, Name = "Deploy", State = state, QueueTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), StartTime = new DateTimeOffset(2026, 1, 2, 3, 4, 6, TimeSpan.Zero), CompletedTime = new DateTimeOffset(2026, 1, 2, 3, 5, 6, TimeSpan.Zero), DetailFingerprint = detailFingerprint };

    // What confirmation stores: the release's notes fingerprint, the history unit's task fingerprint on the deployment.
    private static string FingerprintOf(OctopusResourceNode node, IReadOnlyDictionary<string, OctopusResourceNode> nodes)
        => node.Kind switch
        {
            OctopusResourceKind.Release => OctopusImportFingerprint.ForRelease(node.GetSource<OctopusReleaseDto>()),
            OctopusResourceKind.Deployment => OctopusImportFingerprint.ForTask(nodes[node.GetSource<OctopusDeploymentDto>().TaskId].GetSource<OctopusServerTaskDto>()),
            _ => null
        };

    public const int SpaceId = 7;

    public const string ExportPassword = "scenario-export-password";

    public static string LibraryVariableSetId(string libraryId) => $"variableset-{libraryId}";

    public static string ProjectVariableSetId(string projectId) => $"variableset-{projectId}";

    public static object Var(string id, string name, string value, Dictionary<string, string[]> scope = null)
        => new { Id = id, Name = name, Value = value, Type = "String", Scope = scope ?? new Dictionary<string, string[]>() };

    public static object Secret(string id, string name, string encryptedValue, Dictionary<string, string[]> scope = null)
        => new { Id = id, Name = name, Value = encryptedValue, Type = "Sensitive", IsSensitive = true, Scope = scope ?? new Dictionary<string, string[]>() };

    public static Dictionary<string, string[]> Scope(string key, params string[] values) => new() { [key] = values };

    public static object ScriptAction(string id, string scriptBody, Dictionary<string, string> extraProperties = null)
    {
        var properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = scriptBody, ["Octopus.Action.RunOnServer"] = "true" };

        foreach (var property in extraProperties ?? [])
            properties[property.Key] = property.Value;

        return new { Id = id, Name = id, ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = properties };
    }

    /// <summary>A script step that pulls one package from <paramref name="feedId"/>.</summary>
    public static object PackageScriptAction(string id, string feedId)
        => new { Id = id, Name = id, ActionType = "Octopus.Script", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = new[] { new { Id = $"{id}-package", Name = "", PackageId = "App", FeedId = feedId, AcquisitionLocation = "Server" } }, Properties = new Dictionary<string, string> { ["Octopus.Action.Script.ScriptSource"] = "Inline", ["Octopus.Action.Script.Syntax"] = "Bash", ["Octopus.Action.Script.ScriptBody"] = "echo hi", ["Octopus.Action.RunOnServer"] = "true" } };

    public static object TypedVar(string id, string name, string type, string value, Dictionary<string, string[]> scope = null)
        => new { Id = id, Name = name, Value = value, Type = type, Scope = scope ?? new Dictionary<string, string[]>() };

    public static object ManualAction(string id, string responsibleTeamIds)
        => new { Id = id, Name = id, ActionType = "Octopus.Manual", Environments = Array.Empty<string>(), ExcludedEnvironments = Array.Empty<string>(), Channels = Array.Empty<string>(), Packages = Array.Empty<object>(), Properties = new Dictionary<string, string> { ["Octopus.Action.Manual.Instructions"] = "Approve", ["Octopus.Action.Manual.ResponsibleTeamIds"] = responsibleTeamIds, ["Octopus.Action.RunOnServer"] = "true" } };

    public static string Encrypt(string plaintext) => Secrets.OctopusExportTestCipher.Encrypt(plaintext, ExportPassword);

    /// <param name="pairingConfirmed">The user confirmed the export-file pairing the preview asked for (posted back with the plan).</param>
    /// <param name="selections">What the user selected among the resources the plan offers to decide on.</param>
    /// <param name="chosenProjectIds">The session's project selection; null plans the whole space.</param>
    public async Task<ScenarioResult> PlanAsync(OctopusExportSecrets secrets = null, bool pairingConfirmed = false, OctopusImportChoiceSelections selections = null, IReadOnlyCollection<string> chosenProjectIds = null)
    {
        var graph = await BuildGraphAsync().ConfigureAwait(false);
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph, chosenProjectIds);
        var conflicts = await DiscoverConflictsAsync(graph).ConfigureAwait(false);
        var accessor = new OctopusExportSecretsAccessor { Secrets = secrets ?? OctopusExportSecrets.None };
        var preview = new OctopusImportPreviewPlanner(accessor).BuildPreviewPlan(dependencyPlan, conflicts, selections);

        if (pairingConfirmed && preview.FillPairing != null) preview.FillPairing.Confirmed = true;

        var validation = new OctopusImportPreviewValidator(accessor).Validate(graph, dependencyPlan, conflicts, preview);

        return new ScenarioResult(graph, dependencyPlan, conflicts, preview, validation);
    }

    public async Task<OctopusResourceGraph> BuildGraphAsync()
    {
        var entries = new List<OctopusExtractedArchiveEntry> { new("manifest.json", Encoding.UTF8.GetBytes(BuildManifestJson())) };
        entries.AddRange(_documents.Select(d => new OctopusExtractedArchiveEntry($"{d.Id}.json", Encoding.UTF8.GetBytes(WithSpaceId(d.Json)))));

        var extraction = await new OctopusInputExtractor().ExtractJsonEntriesAsync(entries).ConfigureAwait(false);

        return new OctopusResourceGraphBuilder().Build(new OctopusManifestInventoryBuilder().Build(extraction));
    }

    private async Task<OctopusImportConflictDiscoveryResult> DiscoverConflictsAsync(OctopusResourceGraph graph)
    {
        var destination = new Mock<IOctopusImportDestinationDataProvider>();
        destination.Setup(d => d.GetResourcesAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(_destination);
        destination.Setup(d => d.GetReleaseKeysAsync(SpaceId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(_releaseKeys);
        destination.Setup(d => d.GetChildNamesAsync(SpaceId, It.IsAny<OctopusResourceKind>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).Callback((int _, OctopusResourceKind kind, IReadOnlyCollection<int> ownerIds, CancellationToken _) => ChildNameReads.Add((kind, ownerIds.ToList()))).ReturnsAsync((int _, OctopusResourceKind kind, IReadOnlyCollection<int> ownerIds, CancellationToken _) => SquidChildren(kind).Where(c => ownerIds.Contains(c.OwnerId)).Distinct().ToList());
        destination.Setup(d => d.GetOrphanedHistoryAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(() => new OctopusImportOrphanedHistory(_orphanedReleases.ToHashSet(), _orphanedDeployments.ToHashSet()));
        destination.Setup(d => d.GetStoredHistoryAsync(SpaceId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync((int _, IReadOnlyCollection<int> releaseIds, IReadOnlyCollection<int> taskIds, CancellationToken _) => StoredHistory(releaseIds, taskIds));

        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportPriorMappings(PriorRows(graph), _priorInstallationId, _priorExportSourceIds, _deletedInSquid));

        var variables = new Mock<IVariableService>();
        variables.Setup(v => v.GetSensitiveVariableSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => _squidVariables.Where(v => ids.Contains(v.VariableSetId)).ToList());

        var feeds = new Mock<IExternalFeedService>();
        feeds.Setup(f => f.GetCredentialSlotsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync((IReadOnlyCollection<int> ids, CancellationToken _) => _squidFeeds.Where(f => ids.Contains(f.FeedId)).ToList());

        var sessions = new Mock<IOctopusImportSessionDataProvider>();
        sessions.Setup(s => s.GetLatestHistoryFetchAsync(SpaceId, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((int _, string installationId, CancellationToken _) => new[] { _priorImportSession, _sourceImportSession }.Concat(_otherImportSessions).FirstOrDefault(fetch => fetch != null && string.Equals(installationId, fetch.InstallationId, StringComparison.OrdinalIgnoreCase)));

        return await new OctopusImportConflictDiscoveryService(destination.Object, sourceMap.Object, new Lazy<IVariableService>(() => variables.Object), new Lazy<IExternalFeedService>(() => feeds.Object), sessions.Object).DiscoverAsync(SpaceId, graph).ConfigureAwait(false);
    }

    // Variables come from the sensitive slots too (a secret Squid holds is a variable it holds by name).
    private IEnumerable<OctopusImportDestinationChild> SquidChildren(OctopusResourceKind kind)
        => _squidChildren.Where(c => c.Kind == kind.ToString()).Concat(kind == OctopusResourceKind.Variable ? _squidVariables.Select(v => new OctopusImportDestinationChild(kind.ToString(), v.VariableSetId, v.Identity.Name) { Id = v.VariableId, Scopes = v.Identity.Scopes }) : []);

    private Dictionary<(string Type, string Id), OctopusImportPriorMapping> PriorRows(OctopusResourceGraph graph)
        => _prior.ToDictionary(pair => pair.Key, pair => _rowsOfUnknownOrigin || HasCreator(pair.Value) ? pair.Value : WithDefaultCreator(pair.Value, graph));

    private OctopusImportPriorMapping WithDefaultCreator(OctopusImportPriorMapping row, OctopusResourceGraph graph)
    {
        var recorded = _priorInstallationId != null || _priorExportSourceIds.Count > 0;

        return recorded ? row with { CreatedInstallationId = _priorInstallationId, CreatedExportSourceId = _priorExportSourceIds.FirstOrDefault() } : row with { CreatedInstallationId = graph.InstallationId, CreatedExportSourceId = graph.ExportSourceId };
    }

    private static bool HasCreator(OctopusImportPriorMapping row) => row.CreatedInstallationId != null || row.CreatedExportSourceId != null;

    private OctopusImportStoredHistory StoredHistory(IReadOnlyCollection<int> releaseIds, IReadOnlyCollection<int> taskIds)
    {
        StoredHistoryReads.Add((releaseIds.ToList(), taskIds.ToList()));

        return new OctopusImportStoredHistory(_storedReleaseNotes.Where(r => releaseIds.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value), _storedTasks.Where(t => taskIds.Contains(t.Key)).ToDictionary(t => t.Key, t => t.Value));
    }

    private string WithSpaceId(string json)
    {
        if (_exportSpaceId == null) return json;

        var node = JsonNode.Parse(json)!.AsObject();
        node["SpaceId"] = _exportSpaceId;

        return node.ToJsonString();
    }

    private OctopusImportScenario Add(string id, string documentType, object document)
    {
        _documents.Add((id, documentType, JsonSerializer.Serialize(document)));
        return this;
    }

    private string BuildManifestJson()
        => JsonSerializer.Serialize(new
        {
            SchemaVersions = Array.Empty<string>(),
            InstallationId = _installationId,
            Entries = _documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(WithSpaceId(d.Json)))).ToLowerInvariant() })
        });

    internal sealed record ScenarioResult(OctopusResourceGraph Graph, OctopusImportDependencyPlan DependencyPlan, OctopusImportConflictDiscoveryResult Conflicts, OctopusImportPreviewPlanDto Preview, OctopusImportValidationResultDto Validation)
    {
        public IEnumerable<OctopusImportDiagnosticDto> AllDiagnostics => Preview.Diagnostics.Concat(Preview.Resources.SelectMany(r => r.Diagnostics)).Concat(Validation.Diagnostics);

        public IEnumerable<OctopusImportDiagnosticDto> Blockers => AllDiagnostics.Where(d => d.Severity == Squid.Message.Enums.OctopusImport.OctopusImportCompatibilitySeverity.Blocker);

        public bool BlocksConfirmation => Preview.HasBlockers || Validation.HasBlockers;

        public OctopusImportResourceResultDto Resource(string sourceId) => Preview.Resources.Single(r => r.SourceId == sourceId);
    }
}
