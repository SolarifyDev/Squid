using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportPreviewPlannerTests
{
    private readonly OctopusImportPreviewPlanner _planner = new();

    [Fact]
    public void BuildPreviewPlan_WhenResourceHasNoConflict_ProposesCreate()
    {
        var resource = Node("Environments-1", OctopusResourceKind.Environment, "Development");

        var preview = _planner.BuildPreviewPlan(Plan([resource]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.DestinationId.ShouldBeNull();
        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void BuildPreviewPlan_WhenCurrentSensitiveVariableIsSelected_AddsRequiredInputMarker()
    {
        var variable = new OctopusVariableDto
        {
            Id = "Variables-Secret",
            Name = "ApiKey",
            Type = "Sensitive",
            IsSensitive = true,
            Value = "preview-source-secret",
            Scope = { ["Environment"] = ["Environments-1"] }
        };
        var resource = Node("Variables-Secret", OctopusResourceKind.Variable, "ApiKey", source: variable);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), NoConflicts());

        var resourceResult = preview.Resources.Single();
        var requiredInput = resourceResult.RequiredInputs.Single();
        preview.RequiredInputs.Single().InputKey.ShouldBe(requiredInput.InputKey);
        requiredInput.Kind.ShouldBe(OctopusImportRequiredInputKind.SensitiveVariableValue);
        requiredInput.Name.ShouldBe("ApiKey");
        requiredInput.ValueType.ShouldBe("Sensitive");
        requiredInput.HasSourceValue.ShouldBeTrue();
        requiredInput.SourceScopes["Environment"].ShouldBe(["Environments-1"]);
    }

    [Fact]
    public void BuildPreviewPlan_WhenHistoricalSensitiveVariableIsOutOfScope_DoesNotRequireInput()
    {
        var variable = new OctopusVariableDto
        {
            Id = "Variables-Secret",
            Name = "ApiKey",
            Type = "Sensitive",
            IsSensitive = true,
            Value = "historical-source-secret"
        };
        var resource = Node("Variables-Secret", OctopusResourceKind.Variable, "ApiKey", isHistorical: true, source: variable);

        var preview = _planner.BuildPreviewPlan(Plan([], outOfScopeResources: [resource]), NoConflicts());

        preview.RequiredInputs.ShouldBeEmpty();
        preview.Resources.Single().RequiredInputs.ShouldBeEmpty();
    }

    [Fact]
    public void BuildPreviewPlan_WhenSharedResourceHasOneConflict_ProposesReuseExisting()
    {
        var resource = Node("Feeds-1", OctopusResourceKind.Feed, "Docker");
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(resource, Match(200, OctopusResourceKind.Feed, "Docker", "docker"))
        ]);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.DestinationId.ShouldBe(200);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ReuseExistingResource);
    }

    [Fact]
    public void BuildPreviewPlan_WhenProjectConflicts_RequiresRenameAndAddsBlocker()
    {
        var resource = Node("Projects-1", OctopusResourceKind.Project, "My Project");
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(resource, Match(300, OctopusResourceKind.Project, "My Project", "my-project"))
        ]);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.DestinationId.ShouldBeNull();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject);
        preview.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_WhenSharedResourceHasAmbiguousConflicts_RequiresRename()
    {
        var resource = Node("ProjectGroups-1", OctopusResourceKind.ProjectGroup, "Default");
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(
                resource,
                Match(400, OctopusResourceKind.ProjectGroup, "Default", "default-a"),
                Match(401, OctopusResourceKind.ProjectGroup, "Default B", "default"))
        ]);

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.RenameRequired);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RenameRequiredForAmbiguousConflict);
    }

    [Fact]
    public void BuildPreviewPlan_WhenResourceIsHistoricalOrOutOfScope_ProposesSkip()
    {
        var release = Node("Releases-1", OctopusResourceKind.Release, "1.0.0", isHistorical: true);

        var preview = _planner.BuildPreviewPlan(Plan([], outOfScopeResources: [release]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceOutOfScope);
    }

    [Fact]
    public void BuildPreviewPlan_WhenResourceIsWorkerPool_ProposesSkip()
    {
        var workerPool = Node("WorkerPools-1", OctopusResourceKind.WorkerPool, "Default Worker Pool");

        var preview = _planner.BuildPreviewPlan(Plan([], outOfScopeResources: [workerPool]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceOutOfScope);
    }

    [Fact]
    public void BuildPreviewPlan_IncludesOutOfScopeResourcesAsSkippedPreviewResults()
    {
        var currentProcess = Node("deploymentprocess-Projects-1", OctopusResourceKind.DeploymentProcess, "Current process");
        var frozenProcess = Node("deploymentprocess-Projects-1-s-1-ABC", OctopusResourceKind.DeploymentProcessSnapshot, "Frozen process", isHistorical: true);
        var release = Node("Releases-1", OctopusResourceKind.Release, "1.0.0", isHistorical: true);

        var preview = _planner.BuildPreviewPlan(Plan([currentProcess], outOfScopeResources: [frozenProcess, release]), NoConflicts());

        preview.Resources.Single(r => r.SourceId == currentProcess.SourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        preview.Resources.Single(r => r.SourceId == frozenProcess.SourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        preview.Resources.Single(r => r.SourceId == release.SourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        preview.Resources.Where(r => r.PreviewAction == OctopusImportPreviewAction.Skip)
            .SelectMany(r => r.Diagnostics)
            .All(d => d.Code == OctopusImportPreviewDiagnosticCodes.ResourceOutOfScope)
            .ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_WhenResourceKindIsUnsupported_ProposesUnsupported()
    {
        var certificate = Node("Certificates-1", OctopusResourceKind.Certificate, "TLS");

        var preview = _planner.BuildPreviewPlan(Plan([certificate]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Unsupported);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Unsupported);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceUnsupported);
    }

    [Fact]
    public void BuildPreviewPlan_AddsManualConfigurationDiagnosticsForExternalResourceSecrets()
    {
        var feed = Node(new OctopusFeedDto
        {
            Id = "Feeds-1",
            Name = "Docker",
            Username = "feed-user",
            Password = "feed-source-secret"
        }, OctopusResourceKind.Feed);
        var account = Node(new OctopusAccountDto
        {
            Id = "Accounts-1",
            Name = "AWS",
            Credentials = Json("""{ "SecretKey": "account-source-secret" }""")
        }, OctopusResourceKind.Account);
        var certificate = Node(new OctopusCertificateDto
        {
            Id = "Certificates-1",
            Name = "TLS",
            HasPrivateKey = true,
            CertificateData = Json("""{ "Pfx": "certificate-source-secret" }""")
        }, OctopusResourceKind.Certificate);
        var target = Node(new OctopusMachineDto
        {
            Id = "Machines-1",
            Name = "Kubernetes target",
            Endpoint = Json("""{ "ProviderConfig": { "Token": "endpoint-source-secret" } }""")
        }, OctopusResourceKind.Machine);

        var preview = _planner.BuildPreviewPlan(Plan([feed, account, certificate, target]), NoConflicts());

        preview.Resources.Single(r => r.SourceId == "Feeds-1").Diagnostics.Select(d => d.Code)
            .ShouldContain(OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted);
        preview.Resources.Single(r => r.SourceId == "Accounts-1").Diagnostics.Select(d => d.Code)
            .ShouldContain(OctopusImportRedactionDiagnosticCodes.AccountCredentialsOmitted);
        preview.Resources.Single(r => r.SourceId == "Certificates-1").Diagnostics.Select(d => d.Code)
            .ShouldContain(OctopusImportRedactionDiagnosticCodes.CertificatePrivateMaterialOmitted);
        preview.Resources.Single(r => r.SourceId == "Machines-1").Diagnostics.Select(d => d.Code)
            .ShouldContain(OctopusImportRedactionDiagnosticCodes.EndpointSecretOmitted);
        preview.Resources.SelectMany(r => r.Diagnostics).All(d =>
            !d.Message.Contains("source-secret", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_WhenDependencyPlanHasResourceBlocker_ProposesBlocked()
    {
        var project = Node("Projects-1", OctopusResourceKind.Project, "Project");
        var dependencyDiagnostic = new OctopusInputExtractionDiagnostic(
            OctopusImportCompatibilitySeverity.Blocker,
            "octopus.test.blocker",
            "Dependency graph is blocked.",
            SourceId: project.SourceId,
            DocumentKind: OctopusDocumentKind.Project);

        var preview = _planner.BuildPreviewPlan(Plan([project], [dependencyDiagnostic]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Blocked);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Blocked);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceBlockedByDependencyPlan);
        preview.Diagnostics.Single().Code.ShouldBe("octopus.test.blocker");
        preview.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_ReturnsResourcesInDependencyOrderRank()
    {
        var resources = new[]
        {
            Node("Actions-1", OctopusResourceKind.DeploymentAction, "Action"),
            Node("Projects-1", OctopusResourceKind.Project, "Project"),
            Node("Environments-1", OctopusResourceKind.Environment, "Development")
        };

        var preview = _planner.BuildPreviewPlan(Plan(resources), NoConflicts());

        preview.Resources.Select(r => r.SourceId).ToList().ShouldBe(["Environments-1", "Projects-1", "Actions-1"]);
    }

    private static OctopusImportDependencyPlan Plan(
        IReadOnlyList<OctopusResourceNode> resources,
        IReadOnlyList<OctopusInputExtractionDiagnostic> diagnostics = null,
        IReadOnlyList<OctopusResourceNode> outOfScopeResources = null)
        => new(resources, [], [], diagnostics ?? [], outOfScopeResources ?? []);

    private static OctopusImportConflictDiscoveryResult NoConflicts()
        => new([]);

    private static OctopusImportResourceConflict Conflict(
        OctopusResourceNode resource,
        params OctopusImportDestinationMatch[] matches)
        => new(resource, matches);

    private static OctopusImportDestinationMatch Match(
        int destinationId,
        OctopusResourceKind kind,
        string name,
        string slug)
        => new(
            new OctopusImportDestinationResource(
                destinationId,
                7,
                kind,
                name,
                slug,
                DateTimeOffset.UtcNow),
            OctopusImportIdentityMatchKind.NameAndSlug);

    private static OctopusResourceNode Node(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        bool isHistorical = false,
        object source = null)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            "Projects-1",
            null,
            isHistorical,
            source ?? new object());

    private static OctopusResourceNode Node<T>(T source, OctopusResourceKind kind, bool isHistorical = false)
        where T : OctopusDocumentDto
        => new(
            source.Id,
            source.Name,
            kind,
            DocumentKind(kind),
            $"{source.Id}.json",
            "Projects-1",
            null,
            isHistorical,
            source);

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var document = System.Text.Json.JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static OctopusDocumentKind DocumentKind(OctopusResourceKind kind)
        => kind switch
        {
            OctopusResourceKind.Feed => OctopusDocumentKind.Feed,
            OctopusResourceKind.Account => OctopusDocumentKind.Account,
            OctopusResourceKind.Certificate => OctopusDocumentKind.Certificate,
            OctopusResourceKind.Machine => OctopusDocumentKind.Machine,
            _ => OctopusDocumentKind.Project
        };
}
