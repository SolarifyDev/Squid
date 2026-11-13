using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.Core.Services.OctopusImport.SourceMap;
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
    private string _installationId;
    private string _priorInstallationId;

    public OctopusImportScenario Environment(string id, string name)
        => Add(id, "StaticDeploymentEnvironment", new { Id = id, Name = name, Slug = name.ToLowerInvariant() });

    public OctopusImportScenario Machine(string id, string name, params string[] roles)
        => Add(id, "Machine", new { Id = id, Name = name, Roles = roles, EnvironmentIds = Array.Empty<string>() });

    public OctopusImportScenario Channel(string id, string projectId, string name)
        => Add(id, "Channel", new { Id = id, Name = name, ProjectId = projectId, IsDefault = false, Slug = name.ToLowerInvariant() });

    public OctopusImportScenario Feed(string id, string name, string feedType)
        => Add(id, "Feed", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), FeedType = feedType, FeedUri = "https://packages.example.com" });

    public OctopusImportScenario Account(string id, string name, string accountType, params string[] environmentIds)
        => Add(id, "Account", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), AccountType = accountType, EnvironmentIds = environmentIds });

    public OctopusImportScenario Lifecycle(string id, string name, params string[] environmentIds)
        => Add(id, "Lifecycle", new { Id = id, Name = name, Slug = name.ToLowerInvariant(), Phases = new[] { new { Id = $"{id}-phase", Name = "Phase", AutomaticDeploymentTargets = environmentIds, OptionalDeploymentTargets = Array.Empty<string>() } } });

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

    /// <summary>A source-map row an earlier confirmation recorded.</summary>
    public OctopusImportScenario ExistingMapping(OctopusResourceKind kind, string sourceId, int destinationId, string fingerprint = null, string detailFingerprint = null)
    {
        _prior[(kind.ToString(), sourceId)] = new OctopusImportPriorMapping(destinationId, "Created", fingerprint, detailFingerprint);
        return this;
    }

    public OctopusImportScenario WithoutMapping(OctopusResourceKind kind, string sourceId)
    {
        _prior.Remove((kind.ToString(), sourceId));
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

    /// <summary>The Octopus installation an earlier import of this space recorded.</summary>
    public OctopusImportScenario PriorInstallation(string installationId)
    {
        _priorInstallationId = installationId;
        return this;
    }

    /// <summary>Records every resource a plan writes as imported, the way confirmation fills the source map.</summary>
    public OctopusImportScenario ImportedAs(ScenarioResult plan)
    {
        var nodes = plan.Graph.Resources.ToDictionary(n => n.SourceId, StringComparer.OrdinalIgnoreCase);
        var destinationId = 1000;

        foreach (var resource in plan.Preview.Resources.Where(r => r.PreviewAction == Squid.Message.Enums.OctopusImport.OctopusImportPreviewAction.Create))
            ExistingMapping(Enum.Parse<OctopusResourceKind>(resource.SourceType), resource.SourceId, ++destinationId, FingerprintOf(nodes[resource.SourceId], nodes));

        return this;
    }

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

    public async Task<ScenarioResult> PlanAsync(OctopusExportSecrets secrets = null)
    {
        var graph = await BuildGraphAsync().ConfigureAwait(false);
        var dependencyPlan = new OctopusImportDependencyPlanner().BuildCurrentConfigurationPlan(graph);
        var conflicts = await DiscoverConflictsAsync(graph).ConfigureAwait(false);
        var accessor = new OctopusExportSecretsAccessor { Secrets = secrets ?? OctopusExportSecrets.None };
        var preview = new OctopusImportPreviewPlanner(accessor).BuildPreviewPlan(dependencyPlan, conflicts);
        var validation = new OctopusImportPreviewValidator(accessor).Validate(graph, dependencyPlan, conflicts, preview);

        return new ScenarioResult(graph, dependencyPlan, conflicts, preview, validation);
    }

    public async Task<OctopusResourceGraph> BuildGraphAsync()
    {
        var entries = new List<OctopusExtractedArchiveEntry> { new("manifest.json", Encoding.UTF8.GetBytes(BuildManifestJson())) };
        entries.AddRange(_documents.Select(d => new OctopusExtractedArchiveEntry($"{d.Id}.json", Encoding.UTF8.GetBytes(d.Json))));

        var extraction = await new OctopusInputExtractor().ExtractJsonEntriesAsync(entries).ConfigureAwait(false);

        return new OctopusResourceGraphBuilder().Build(new OctopusManifestInventoryBuilder().Build(extraction));
    }

    private async Task<OctopusImportConflictDiscoveryResult> DiscoverConflictsAsync(OctopusResourceGraph graph)
    {
        var destination = new Mock<IOctopusImportDestinationDataProvider>();
        destination.Setup(d => d.GetResourcesAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(_destination);
        destination.Setup(d => d.GetReleaseKeysAsync(SpaceId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>())).ReturnsAsync(_releaseKeys);

        var sourceMap = new Mock<IOctopusImportSourceMapDataProvider>();
        sourceMap.Setup(s => s.LoadAsync(SpaceId, It.IsAny<CancellationToken>())).ReturnsAsync(new OctopusImportPriorMappings(new Dictionary<(string Type, string Id), OctopusImportPriorMapping>(_prior), _priorInstallationId));

        return await new OctopusImportConflictDiscoveryService(destination.Object, sourceMap.Object).DiscoverAsync(SpaceId, graph).ConfigureAwait(false);
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
            Entries = _documents.Select(d => new { d.Id, Name = d.Id, d.DocumentType, ExportType = "FullDocument", DocumentSource = $"{d.Id}.json", ParentId = (string)null, Hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(d.Json))).ToLowerInvariant() })
        });

    internal sealed record ScenarioResult(OctopusResourceGraph Graph, OctopusImportDependencyPlan DependencyPlan, OctopusImportConflictDiscoveryResult Conflicts, OctopusImportPreviewPlanDto Preview, OctopusImportValidationResultDto Validation)
    {
        public IEnumerable<OctopusImportDiagnosticDto> AllDiagnostics => Preview.Diagnostics.Concat(Preview.Resources.SelectMany(r => r.Diagnostics)).Concat(Validation.Diagnostics);

        public IEnumerable<OctopusImportDiagnosticDto> Blockers => AllDiagnostics.Where(d => d.Severity == Squid.Message.Enums.OctopusImport.OctopusImportCompatibilitySeverity.Blocker);

        public bool BlocksConfirmation => Preview.HasBlockers || Validation.HasBlockers;

        public OctopusImportResourceResultDto Resource(string sourceId) => Preview.Resources.Single(r => r.SourceId == sourceId);
    }
}
