using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.UnitTests.Services.OctopusImport.Secrets;
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
        resourceResult.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing);
        resourceResult.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        preview.HasBlockers.ShouldBeTrue();
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
    public void BuildPreviewPlan_WhenHistoricalSnapshotOrOutOfScope_ProposesSkip()
    {
        var release = Node("deploymentprocess-Projects-1-s-1-ABC", OctopusResourceKind.DeploymentProcessSnapshot, "Frozen process", isHistorical: true);

        var preview = _planner.BuildPreviewPlan(Plan([], outOfScopeResources: [release]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceOutOfScope);
    }

    [Fact]
    public void BuildPreviewPlan_WhenReleaseIsHistorical_ProposesCreate()
    {
        var release = HistoryNode(new OctopusReleaseDto { Id = "Releases-1", Name = "1.0.0", Version = "1.0.0", ProjectId = "Projects-1", ChannelId = "Channels-1" }, OctopusResourceKind.Release);

        var preview = _planner.BuildPreviewPlan(Plan([.. HistoryOwners(), release]), NoConflicts());

        var result = preview.Resources.Single(r => r.SourceId == release.SourceId);
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.Diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(OctopusResourceKind.Deployment, "Deployments-1")]
    [InlineData(OctopusResourceKind.ServerTask, "ServerTasks-1")]
    public void BuildPreviewPlan_WhenDeploymentHistoryIsHistorical_ProposesCreate(
        OctopusResourceKind kind,
        string sourceId)
    {
        var history = HistoryUnit();

        var preview = _planner.BuildPreviewPlan(Plan([.. HistoryOwners(), .. history]), NoConflicts());

        var result = preview.Resources.Single(r => r.SourceId == sourceId && r.SourceType == kind.ToString());
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Pending);
        result.Diagnostics.ShouldBeEmpty();
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
        var release = HistoryNode(new OctopusReleaseDto { Id = "Releases-1", Name = "1.0.0", Version = "1.0.0", ProjectId = "Projects-1", ChannelId = "Channels-1" }, OctopusResourceKind.Release);

        var preview = _planner.BuildPreviewPlan(Plan([.. HistoryOwners(), currentProcess, release], outOfScopeResources: [frozenProcess]), NoConflicts());

        preview.Resources.Single(r => r.SourceId == currentProcess.SourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        preview.Resources.Single(r => r.SourceId == frozenProcess.SourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        preview.Resources.Single(r => r.SourceId == release.SourceId).PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
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

    [Theory]
    [InlineData(OctopusResourceKind.Machine, "Machines-1")]
    [InlineData(OctopusResourceKind.ActionTemplate, "ActionTemplates-1")]
    [InlineData(OctopusResourceKind.Tenant, "Tenants-1")]
    [InlineData(OctopusResourceKind.Runbook, "Runbooks-1")]
    [InlineData(OctopusResourceKind.Trigger, "ProjectTriggers-1")]
    public void BuildPreviewPlan_WhenResourceHasNoConfirmationImplementation_ProposesUnsupported(
        OctopusResourceKind kind,
        string sourceId)
    {
        var resource = Node(sourceId, kind, kind.ToString());

        var preview = _planner.BuildPreviewPlan(Plan([resource]), NoConflicts());

        var result = preview.Resources.Single();
        result.PreviewAction.ShouldBe(OctopusImportPreviewAction.Unsupported);
        result.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Unsupported);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.ResourceUnsupported);
    }

    [Fact]
    public void BuildPreviewPlan_AddsManualConfigurationDiagnosticsForExternalResourceSecrets()
    {
        var feed = Node(new OctopusFeedDto
        {
            Id = "Feeds-1",
            Name = "Docker",
            FeedType = "Docker",
            Username = "feed-user",
            Password = "feed-source-secret"
        }, OctopusResourceKind.Feed);
        var account = Node(new OctopusAccountDto
        {
            Id = "Accounts-1",
            Name = "AWS",
            AccountType = "AmazonWebServicesAccount",
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
            Node("Variables-1", OctopusResourceKind.Variable, "Variable"),
            Node("variableset-Projects-1", OctopusResourceKind.VariableSet, "Variables"),
            Node("Actions-1", OctopusResourceKind.DeploymentAction, "Action"),
            Node("deploymentprocess-Projects-1", OctopusResourceKind.DeploymentProcess, "Process"),
            Node("Projects-1", OctopusResourceKind.Project, "Project"),
            Node("Environments-1", OctopusResourceKind.Environment, "Development")
        };

        var preview = _planner.BuildPreviewPlan(Plan(resources), NoConflicts());

        preview.Resources.Select(r => r.SourceId).ToList().ShouldBe([
            "Environments-1",
            "Projects-1",
            "deploymentprocess-Projects-1",
            "Actions-1",
            "variableset-Projects-1",
            "Variables-1"
        ]);
    }

    [Fact]
    public void BuildPreviewPlan_SensitiveVariableDecryptableWithExportPassword_IsNotARequiredInput()
    {
        var planner = new OctopusImportPreviewPlanner(new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.FromPassword("pw") });
        var variable = Node("Variables-1", OctopusResourceKind.Variable, "Db:Password", source: new OctopusVariableDto { Id = "Variables-1", Name = "Db:Password", Type = "Sensitive", Value = OctopusExportTestCipher.Encrypt("secret", "pw") });

        var preview = planner.BuildPreviewPlan(Plan([variable]), NoConflicts());

        preview.HasBlockers.ShouldBeFalse();
        preview.RequiredInputs.ShouldBeEmpty();
        preview.Resources.Single().Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.SensitiveVariableValueDecrypted);
        preview.Resources.Single().Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Info);
    }

    [Fact]
    public void BuildPreviewPlan_SensitiveVariableEncryptedWithAnotherPassword_StaysBlocked()
    {
        var planner = new OctopusImportPreviewPlanner(new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.FromPassword("pw") });
        var variable = Node("Variables-1", OctopusResourceKind.Variable, "Db:Password", source: new OctopusVariableDto { Id = "Variables-1", Name = "Db:Password", Type = "Sensitive", Value = OctopusExportTestCipher.Encrypt("secret", "other") });

        var preview = planner.BuildPreviewPlan(Plan([variable]), NoConflicts());

        preview.HasBlockers.ShouldBeTrue();
        preview.RequiredInputs.Single().SourceId.ShouldBe("Variables-1");
        preview.Resources.Single().Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing);
    }

    [Fact]
    public void BuildPreviewPlan_FeedCredentialsDecryptable_DoesNotWarnTheyWillBeOmitted()
    {
        var feed = new OctopusFeedDto { Id = "Feeds-1", Name = "ACR", FeedType = "Docker", Username = "robot", Password = OctopusExportTestCipher.Encrypt("token", "pw") };
        var withSecrets = new OctopusImportPreviewPlanner(new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.FromPassword("pw") });

        var resolved = withSecrets.BuildPreviewPlan(Plan([Node(feed, OctopusResourceKind.Feed)]), NoConflicts());
        var unresolved = _planner.BuildPreviewPlan(Plan([Node(feed, OctopusResourceKind.Feed)]), NoConflicts());

        resolved.Resources.Single().Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted);
        unresolved.Resources.Single().Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportRedactionDiagnosticCodes.FeedCredentialsOmitted);
    }

    [Theory]
    [InlineData(OctopusResourceKind.Team, "Teams-1")]
    [InlineData(OctopusResourceKind.LibraryVariableSet, "LibraryVariableSets-1")]
    public void BuildPreviewPlan_TeamAndLibraryVariableSet_AreCreatedWhenNoDestinationMatches(OctopusResourceKind kind, string sourceId)
    {
        var preview = _planner.BuildPreviewPlan(Plan([Node(sourceId, kind, kind.ToString())]), NoConflicts());

        preview.Resources.Single().PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        preview.HasBlockers.ShouldBeFalse();
    }

    [Theory]
    [InlineData(OctopusResourceKind.Team, "Teams-1")]
    [InlineData(OctopusResourceKind.LibraryVariableSet, "LibraryVariableSets-1")]
    public void BuildPreviewPlan_TeamAndLibraryVariableSet_ReuseASingleDestinationMatch(OctopusResourceKind kind, string sourceId)
    {
        var resource = Node(sourceId, kind, "Solar");

        var preview = _planner.BuildPreviewPlan(Plan([resource]), new OctopusImportConflictDiscoveryResult([Conflict(resource, Match(77, kind, "Solar", null))]));

        preview.Resources.Single().PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        preview.Resources.Single().DestinationId.ShouldBe(77);
    }

    [Theory]
    [InlineData(0, 0, "system-wide")]
    [InlineData(7, 4, "space 7")]
    public void BuildPreviewPlan_ReusedTeam_WarnsThatItsCurrentMembersCanApprove(int spaceId, int memberCount, string expectedScope)
    {
        var resource = Node("Teams-1", OctopusResourceKind.Team, "Solar");
        var destination = new OctopusImportDestinationResource(77, spaceId, OctopusResourceKind.Team, "Solar Squad", null, DateTimeOffset.UtcNow) { MemberCount = memberCount, MemberFingerprint = "fingerprint-at-preview" };

        var preview = _planner.BuildPreviewPlan(Plan([resource]), new OctopusImportConflictDiscoveryResult([Conflict(resource, new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))]));

        var team = preview.Resources.Single();
        team.DestinationMemberCount.ShouldBe(memberCount);
        team.DestinationMemberFingerprint.ShouldBe("fingerprint-at-preview");
        var warning = team.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval);
        warning.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        warning.Message.ShouldContain("'Solar Squad'");
        warning.Message.ShouldContain(expectedScope);
        warning.Message.ShouldContain($"{memberCount} member(s)");
        preview.HasBlockers.ShouldBeFalse();
    }

    // Everyone means "every user" (each new account is added to it), so its membership is not a snapshot to
    // pin: tracking it would block every import whenever a user is created between preview and confirm.
    [Theory]
    [InlineData(true, 0, "Everyone", true)]
    [InlineData(false, 0, "Everyone", false)]   // a hand-made namesake is an ordinary team
    [InlineData(true, 0, "Squid Administrators", false)]
    [InlineData(true, 7, "Everyone", false)]
    public void BuildPreviewPlan_ReusingTheBuiltInEveryoneTeam_WarnsThatAllUsersCanApprove_WithoutPinningMembership(bool isBuiltIn, int spaceId, string name, bool expectAllUsers)
    {
        var resource = Node("teams-everyone", OctopusResourceKind.Team, "Everyone");
        var destination = new OctopusImportDestinationResource(2, spaceId, OctopusResourceKind.Team, name, null, DateTimeOffset.UtcNow) { IsBuiltIn = isBuiltIn, MemberCount = 12, MemberFingerprint = "fp" };

        var preview = _planner.BuildPreviewPlan(Plan([resource]), new OctopusImportConflictDiscoveryResult([Conflict(resource, new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))]));

        var team = preview.Resources.Single();
        var warning = team.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval);
        warning.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);

        if (expectAllUsers)
        {
            team.DestinationMemberCount.ShouldBeNull();
            team.DestinationMemberFingerprint.ShouldBeNull();
            warning.Message.ShouldContain("every user");
            warning.Message.ShouldNotContain("member(s)");
        }
        else
        {
            team.DestinationMemberCount.ShouldBe(12);
            team.DestinationMemberFingerprint.ShouldBe("fp");
            warning.Message.ShouldContain("12 member(s)");
        }
    }

    [Theory]
    [InlineData(OctopusResourceKind.LibraryVariableSet)]
    [InlineData(OctopusResourceKind.Environment)]
    public void BuildPreviewPlan_ReusedNonTeamResource_HasNoMembershipWarningOrCount(OctopusResourceKind kind)
    {
        var resource = Node("Res-1", kind, "Solar");

        var preview = _planner.BuildPreviewPlan(Plan([resource]), new OctopusImportConflictDiscoveryResult([Conflict(resource, Match(77, kind, "Solar", null))]));

        preview.Resources.Single().DestinationMemberCount.ShouldBeNull();
        preview.Resources.Single().DestinationMemberFingerprint.ShouldBeNull();
        preview.Resources.Single().Diagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReusedTeamGrantsApproval);
    }

    [Fact]
    public void BuildPreviewPlan_CreatedTeam_HasNoMembershipWarning()
    {
        var preview = _planner.BuildPreviewPlan(Plan([Node("Teams-1", OctopusResourceKind.Team, "Solar")]), NoConflicts());

        preview.Resources.Single().Diagnostics.ShouldBeEmpty();
        preview.Resources.Single().DestinationMemberCount.ShouldBeNull();
    }

    [Theory]
    [InlineData("teams-everyone")]
    [InlineData("teams-administrators")]
    public void BuildPreviewPlan_BuiltInTeamWithEquivalent_WithoutDestinationMatch_IsBlockedNeverCreated(string sourceId)
    {
        var preview = _planner.BuildPreviewPlan(Plan([Node(sourceId, OctopusResourceKind.Team, "Built-in")]), NoConflicts());

        var team = preview.Resources.Single();
        team.PreviewAction.ShouldBe(OctopusImportPreviewAction.Blocked);
        team.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Blocked);
        team.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.BuiltInTeamEquivalentMissing).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        preview.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void BuildPreviewPlan_BuiltInTeamWithEquivalent_ReusingTheSquidBuiltIn_IsNotBlocked()
    {
        var resource = Node("teams-everyone", OctopusResourceKind.Team, "Everyone");
        var destination = new OctopusImportDestinationResource(2, 0, OctopusResourceKind.Team, "Everyone", null, DateTimeOffset.UtcNow) { IsBuiltIn = true, MemberCount = 12 };

        var preview = _planner.BuildPreviewPlan(Plan([resource]), new OctopusImportConflictDiscoveryResult([Conflict(resource, new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))]));

        preview.Resources.Single().PreviewAction.ShouldBe(OctopusImportPreviewAction.ReuseExisting);
        preview.HasBlockers.ShouldBeFalse();
    }

    [Theory]
    [InlineData("teams-managers", false)]
    [InlineData("teams-spacemanagers-Spaces-1", false)]
    [InlineData("teams-managers", true)]
    public void BuildPreviewPlan_BuiltInTeamWithoutEquivalent_WarnsWhetherCreatedOrReused(string sourceId, bool reused)
    {
        var resource = Node(sourceId, OctopusResourceKind.Team, "Managers");
        var conflicts = reused ? new OctopusImportConflictDiscoveryResult([Conflict(resource, Match(77, OctopusResourceKind.Team, "Managers", null))]) : NoConflicts();

        var preview = _planner.BuildPreviewPlan(Plan([resource]), conflicts);

        var team = preview.Resources.Single();
        team.PreviewAction.ShouldBe(reused ? OctopusImportPreviewAction.ReuseExisting : OctopusImportPreviewAction.Create);
        team.Diagnostics.Single(d => d.Code == OctopusImportPreviewDiagnosticCodes.BuiltInTeamWithoutEquivalent).Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        preview.HasBlockers.ShouldBeFalse();
    }

    // ── Re-import: prior source-map rows and history units ───────────────────────────────────

    [Fact]
    public async Task ReImportSameExport_AllAlreadyImported()
    {
        var first = await HistoryScenario().PlanAsync();
        first.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(first));
        first.Preview.Resources.ShouldContain(r => r.SourceType == nameof(OctopusResourceKind.ServerTask) && r.PreviewAction == OctopusImportPreviewAction.Create);

        var second = await HistoryScenario().ImportedAs(first).PlanAsync();

        second.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(second));
        foreach (var resource in second.Preview.Resources.Where(r => first.Resource(r.SourceId).PreviewAction == OctopusImportPreviewAction.Create))
        {
            resource.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, customMessage: $"{resource.SourceType} {resource.SourceId}");
            resource.DestinationId.ShouldNotBeNull(customMessage: $"{resource.SourceType} {resource.SourceId}");
        }
    }

    [Fact]
    public async Task ProjectWithMapHit_NotRenameRequired()
    {
        var result = await new OctopusImportScenario()
            .Project("Projects-1", "Web", [], [])
            .ExistingProject(500, "Web")
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 500)
            .PlanAsync();

        var project = result.Resource("Projects-1");
        project.PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        project.DestinationId.ShouldBe(500);
        result.AllDiagnostics.ShouldNotContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Fact]
    public async Task NewChildrenUnderImportedProject_InheritOrCreateUnderTheExistingProject()
    {
        var result = await new OctopusImportScenario()
            .Project("Projects-1", "Web", [], [OctopusImportScenario.Var("Variables-new", "Added", "later")])
            .Channel("Channels-new", "Projects-1", "Hotfix")
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 500)
            .ExistingMapping(OctopusResourceKind.VariableSet, OctopusImportScenario.ProjectVariableSetId("Projects-1"), 501)
            .PlanAsync();

        result.Resource("Variables-new").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, customMessage: "a variable under an imported variable set is never written again");
        result.Resource("Variables-new").DestinationId.ShouldBeNull();
        result.Resource("Variables-new").Diagnostics.ShouldBeEmpty(customMessage: "an already-imported variable is not a reused library variable");
        result.Resource("Channels-new").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create, customMessage: "a channel added in Octopus later is created under the existing project");
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Fact]
    public async Task ChangedReleaseNotes_Update()
    {
        var first = await HistoryScenario(notes: "first notes").PlanAsync();

        var second = await HistoryScenario(notes: "edited in Octopus").ImportedAs(first).PlanAsync();

        var release = second.Resource("Releases-1");
        release.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        release.DestinationId.ShouldNotBeNull();
        second.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        second.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(second));
    }

    [Theory]
    [InlineData("1.0.0", OctopusImportPreviewAction.Skip)]
    [InlineData("0.9.0", OctopusImportPreviewAction.Create)]
    public async Task ReleaseVersionInSquidWithoutMap_SkipsWithWarning(string existingVersion, OctopusImportPreviewAction expected)
    {
        var result = await HistoryScenario()
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 100)
            .ExistingMapping(OctopusResourceKind.Channel, "Channels-1", 200)
            .ExistingMapping(OctopusResourceKind.Environment, "Environments-1", 300)
            .ExistingRelease(100, 200, existingVersion)
            .PlanAsync();

        var release = result.Resource("Releases-1");
        release.PreviewAction.ShouldBe(expected);
        release.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ReleaseVersionExistsInSquid && d.Severity == OctopusImportCompatibilitySeverity.Warning).ShouldBe(expected == OctopusImportPreviewAction.Skip);
        result.Resource("Deployments-1").PreviewAction.ShouldBe(expected, customMessage: "deployments of a skipped release are skipped through the owner rule");
        result.Resource("ServerTasks-1").PreviewAction.ShouldBe(expected);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Theory]
    [InlineData("Projects-1", "Channels-1", "Environments-404", "Releases-1")]
    [InlineData("Projects-1", "Channels-1", "Environments-1", "Releases-404")]
    [InlineData("Projects-1", "Channels-404", "Environments-1", "Releases-1")]
    [InlineData("Projects-404", "Channels-1", "Environments-1", "Releases-1")]
    public async Task HistoryOwnerMissing_Skip(string projectId, string channelId, string environmentId, string releaseId)
    {
        var result = await HistoryScenario(deployment: ("Deployments-2", projectId, channelId, environmentId, releaseId, "ServerTasks-2")).ServerTask("ServerTasks-2").PlanAsync();

        var deployment = result.Resource("Deployments-2");
        deployment.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        deployment.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        deployment.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotImported && d.Severity == OctopusImportCompatibilitySeverity.Warning);
        result.Resource("ServerTasks-2").PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        result.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Theory]
    [InlineData("Projects-404", "Channels-1")]
    [InlineData("Projects-1", "Channels-404")]
    public async Task ReleaseOwnerMissing_Skip(string projectId, string channelId)
    {
        var result = await HistoryScenario().Release("Releases-2", projectId, channelId, "2.0.0").PlanAsync();

        var release = result.Resource("Releases-2");
        release.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        release.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.HistoryOwnerNotImported);
        result.Resource("Releases-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Fact]
    public async Task HistoryOwnerOnlyInTheSourceMap_Resolves()
    {
        var result = await new OctopusImportScenario()
            .Release("Releases-1", "Projects-1", "Channels-1", "1.0.0")
            .Deployment("Deployments-1", "Projects-1", "Channels-1", "Environments-1", "Releases-1", "ServerTasks-1")
            .ServerTask("ServerTasks-1")
            .ExistingMapping(OctopusResourceKind.Project, "Projects-1", 100)
            .ExistingMapping(OctopusResourceKind.Channel, "Channels-1", 200)
            .ExistingMapping(OctopusResourceKind.Environment, "Environments-1", 300)
            .PlanAsync();

        result.Resource("Releases-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.Resource("ServerTasks-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Fact]
    public async Task DeploymentTaskMissing_Skip()
    {
        var result = await HistoryScenario(deployment: ("Deployments-2", "Projects-1", "Channels-1", "Environments-1", "Releases-1", "ServerTasks-404")).PlanAsync();

        var deployment = result.Resource("Deployments-2");
        deployment.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        deployment.Diagnostics.ShouldContain(d => d.Code == OctopusImportPreviewDiagnosticCodes.DeploymentTaskMissing && d.Severity == OctopusImportCompatibilitySeverity.Warning);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Fact]
    public async Task OrphanServerTask_Skip()
    {
        var result = await HistoryScenario().ServerTask("ServerTasks-runbook").PlanAsync();

        var task = result.Resource("ServerTasks-runbook");
        task.PreviewAction.ShouldBe(OctopusImportPreviewAction.Skip);
        task.OutcomeState.ShouldBe(OctopusImportResourceOutcomeState.Skipped);
        result.Resource("ServerTasks-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Create);
        result.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(result));
    }

    [Theory]
    [InlineData("Installation-B", "Installation-A", true)]
    [InlineData("installation-a", "INSTALLATION-A", false)]
    [InlineData(null, "Installation-A", false)]
    [InlineData("Installation-B", null, false)]
    public async Task InstallationMismatch_Blocks(string incoming, string prior, bool expectBlocker)
    {
        var result = await HistoryScenario().Installation(incoming).PriorInstallation(prior).PlanAsync();

        result.Preview.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch && d.Severity == OctopusImportCompatibilitySeverity.Blocker).ShouldBe(expectBlocker);
        result.Validation.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.OctopusInstallationMismatch).ShouldBe(expectBlocker, customMessage: "the client posts the plan, so validation re-checks the installation server-side");
        result.BlocksConfirmation.ShouldBe(expectBlocker);
    }

    [Fact]
    public async Task NullStoredFingerprint_Update()
    {
        var first = await HistoryScenario().PlanAsync();

        var second = await HistoryScenario().ImportedAs(first).ExistingMapping(OctopusResourceKind.Deployment, "Deployments-1", 77).PlanAsync();

        var deployment = second.Resource("Deployments-1");
        deployment.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, customMessage: "a backfilled unit has no fingerprint and is normalised once");
        deployment.DestinationId.ShouldBe(77);
        var task = second.Resource("ServerTasks-1");
        task.PreviewAction.ShouldBe(OctopusImportPreviewAction.Update, customMessage: "the task follows its deployment: one history unit");
        second.Conflicts.PriorMappings.TryGet(nameof(OctopusResourceKind.ServerTask), "ServerTasks-1", out var storedTask).ShouldBeTrue();
        task.DestinationId.ShouldBe(storedTask.DestinationId);
        second.BlocksConfirmation.ShouldBeFalse(customMessage: Blockers(second));
    }

    [Fact]
    public async Task StoredDeploymentWithoutItsTaskRow_Update()
    {
        var first = await HistoryScenario().PlanAsync();
        var scenario = HistoryScenario().ImportedAs(first);

        var withTask = await scenario.PlanAsync();
        withTask.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);

        var withoutTask = await HistoryScenario().ImportedAs(first).WithoutMapping(OctopusResourceKind.ServerTask, "ServerTasks-1").PlanAsync();

        withoutTask.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        withoutTask.Resource("ServerTasks-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
        withoutTask.Resource("ServerTasks-1").DestinationId.ShouldBeNull();
    }

    [Fact]
    public async Task ApiDetailsThenFileWithoutDetails_AlreadyImported()
    {
        var first = await HistoryScenario().PlanAsync();
        var taskFingerprint = OctopusImportFingerprint.ForTask(OctopusImportScenario.TaskDocument("ServerTasks-1"));

        var fileReimport = await HistoryScenario(detailFingerprint: null).ImportedAs(first).ExistingMapping(OctopusResourceKind.Deployment, "Deployments-1", 77, taskFingerprint, "details-from-api").PlanAsync();
        var changedDetails = await HistoryScenario(detailFingerprint: "details-changed").ImportedAs(first).ExistingMapping(OctopusResourceKind.Deployment, "Deployments-1", 77, taskFingerprint, "details-from-api").PlanAsync();

        fileReimport.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported, customMessage: "a file without details never downgrades details fetched from the API");
        fileReimport.Resource("ServerTasks-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.AlreadyImported);
        changedDetails.Resource("Deployments-1").PreviewAction.ShouldBe(OctopusImportPreviewAction.Update);
    }

    [Fact]
    public async Task EveryRow_CarriesItsOwnerProjectSourceId()
    {
        var result = await HistoryScenario().PlanAsync();

        result.Resource("Projects-1").OwnerProjectSourceId.ShouldBe("Projects-1");
        result.Resource("Releases-1").OwnerProjectSourceId.ShouldBe("Projects-1");
        result.Resource("Deployments-1").OwnerProjectSourceId.ShouldBe("Projects-1");
        result.Resource("Environments-1").OwnerProjectSourceId.ShouldBeNull();
    }

    private static OctopusImportScenario HistoryScenario(string notes = "notes", string detailFingerprint = null, (string Id, string ProjectId, string ChannelId, string EnvironmentId, string ReleaseId, string TaskId)? deployment = null)
    {
        var scenario = new OctopusImportScenario()
            .Environment("Environments-1", "Production")
            .Project("Projects-1", "Web", [], [OctopusImportScenario.Var("Variables-1", "Greeting", "hello")], OctopusImportScenario.ScriptAction("Actions-1", "echo hello"))
            .Channel("Channels-1", "Projects-1", "Main")
            .Release("Releases-1", "Projects-1", "Channels-1", "1.0.0", notes)
            .Deployment("Deployments-1", "Projects-1", "Channels-1", "Environments-1", "Releases-1", "ServerTasks-1")
            .ServerTask("ServerTasks-1", detailFingerprint: detailFingerprint);

        return deployment is { } d ? scenario.Deployment(d.Id, d.ProjectId, d.ChannelId, d.EnvironmentId, d.ReleaseId, d.TaskId) : scenario;
    }

    private static string Blockers(OctopusImportScenario.ScenarioResult result)
        => string.Join("; ", result.Blockers.Select(b => $"{b.Code}:{b.SourceId}:{b.Message}"));

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

    // History is imported only under owners that map to Squid rows, so history fixtures carry them.
    private static IReadOnlyList<OctopusResourceNode> HistoryOwners()
        => [Node(new OctopusProjectDto { Id = "Projects-1", Name = "Web" }, OctopusResourceKind.Project), Node(new OctopusChannelDto { Id = "Channels-1", Name = "Main", ProjectId = "Projects-1" }, OctopusResourceKind.Channel), Node(new OctopusEnvironmentDto { Id = "Environments-1", Name = "Production" }, OctopusResourceKind.Environment)];

    private static IReadOnlyList<OctopusResourceNode> HistoryUnit()
        =>
        [
            HistoryNode(new OctopusReleaseDto { Id = "Releases-1", Name = "1.0.0", Version = "1.0.0", ProjectId = "Projects-1", ChannelId = "Channels-1" }, OctopusResourceKind.Release),
            HistoryNode(new OctopusDeploymentDto { Id = "Deployments-1", Name = "Deployment", ProjectId = "Projects-1", ChannelId = "Channels-1", EnvironmentId = "Environments-1", ReleaseId = "Releases-1", TaskId = "ServerTasks-1" }, OctopusResourceKind.Deployment),
            HistoryNode(new OctopusServerTaskDto { Id = "ServerTasks-1", Name = "ServerTask", State = "Success" }, OctopusResourceKind.ServerTask)
        ];

    private static OctopusResourceNode HistoryNode<T>(T source, OctopusResourceKind kind) where T : OctopusDocumentDto => Node(source, kind, isHistorical: true);

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
