using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Secrets;
using Squid.UnitTests.Services.OctopusImport.Secrets;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportPreviewValidatorTests
{
    private readonly OctopusImportPreviewValidator _validator = new();

    [Fact]
    public void Validate_WhenRequiredReferenceIsMissing_AddsBlocker()
    {
        var project = Node("Projects-1", OctopusResourceKind.Project, "Project");
        var graph = Graph(
            [project],
            [
                Reference(
                    project.SourceId,
                    project.Kind,
                    OctopusResourceReferenceKind.Lifecycle,
                    "Lifecycles-Missing",
                    OctopusResourceKind.Lifecycle,
                    isRequired: true)
            ]);

        var result = _validator.Validate(graph, Plan([project]), NoConflicts(), Preview([project]));

        var diagnostic = result.Diagnostics.Single();
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        diagnostic.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.UnresolvedReference);
        diagnostic.SourceId.ShouldBe(project.SourceId);
        result.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WhenTargetRoleHasNoExportedMachine_AddsBlocker()
    {
        var action = Node("Actions-1", OctopusResourceKind.DeploymentAction, "Deploy");
        var graph = Graph(
            [action],
            [
                Reference(
                    action.SourceId,
                    action.Kind,
                    OctopusResourceReferenceKind.TargetRole,
                    "aws-eks-us",
                    null,
                    isRequired: false)
            ]);

        var result = _validator.Validate(graph, Plan([action]), NoConflicts(), Preview([action]));

        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.MissingTargetRole);
    }

    [Fact]
    public void Validate_WhenTargetRoleHasExportedMachine_DoesNotAddBlocker()
    {
        var action = Node("Actions-1", OctopusResourceKind.DeploymentAction, "Deploy");
        var machine = Node("Machines-1", OctopusResourceKind.Machine, "EKS target");
        var graph = Graph(
            [action, machine],
            [
                Reference(
                    action.SourceId,
                    action.Kind,
                    OctopusResourceReferenceKind.TargetRole,
                    "aws-eks-us",
                    null,
                    isRequired: false),
                Reference(
                    machine.SourceId,
                    machine.Kind,
                    OctopusResourceReferenceKind.TargetRole,
                    "aws-eks-us",
                    null,
                    isRequired: false)
            ]);

        var result = _validator.Validate(graph, Plan([action, machine]), NoConflicts(), Preview([action, machine]));

        result.Diagnostics.ShouldBeEmpty();
        result.HasBlockers.ShouldBeFalse();
    }

    [Theory]
    [InlineData(OctopusResourceReferenceKind.Machine, OctopusResourceKind.Machine, "Machines-Missing", OctopusImportPreviewDiagnosticCodes.MissingMachine)]
    [InlineData(OctopusResourceReferenceKind.Account, OctopusResourceKind.Account, "Accounts-Missing", OctopusImportPreviewDiagnosticCodes.MissingAccount)]
    public void Validate_WhenMachineOrAccountReferenceIsMissing_AddsSpecificBlocker(
        OctopusResourceReferenceKind referenceKind,
        OctopusResourceKind targetKind,
        string targetSourceId,
        string expectedCode)
    {
        var variable = Node("Variables-1", OctopusResourceKind.Variable, "Scoped variable");
        var graph = Graph(
            [variable],
            [
                Reference(
                    variable.SourceId,
                    variable.Kind,
                    referenceKind,
                    targetSourceId,
                    targetKind,
                    isRequired: false)
            ]);

        var result = _validator.Validate(graph, Plan([variable]), NoConflicts(), Preview([variable]));

        result.Diagnostics.Single().Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Validate_WhenReuseNoLongerHasSingleDestinationMatch_AddsBlocker()
    {
        var environment = Node("Environments-1", OctopusResourceKind.Environment, "Production");
        var preview = Preview([environment]);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 100;
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(
                environment,
                Match(100, OctopusResourceKind.Environment, "Production"),
                Match(101, OctopusResourceKind.Environment, "Production Copy"))
        ]);

        var result = _validator.Validate(Graph([environment]), Plan([environment]), conflicts, preview);

        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.IncompatibleSharedResourceReuse);
    }

    [Fact]
    public void Validate_WhenReusedResourceNoLongerHasConflictMatch_AddsBlocker()
    {
        var environment = Node("Environments-1", OctopusResourceKind.Environment, "Production");
        var preview = Preview([environment]);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 100;

        var result = _validator.Validate(Graph([environment]), Plan([environment]), NoConflicts(), preview);

        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.IncompatibleSharedResourceReuse);
    }

    [Fact]
    public void Validate_WhenReusedDestinationWasModifiedAfterPreview_AddsStalePlanBlocker()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var feed = Node("Feeds-1", OctopusResourceKind.Feed, "Docker");
        var preview = Preview([feed], generatedAt);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 200;
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(
                feed,
                Match(200, OctopusResourceKind.Feed, "Docker", generatedAt.AddMinutes(1)))
        ]);

        var result = _validator.Validate(Graph([feed]), Plan([feed]), conflicts, preview);

        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
    }

    [Fact]
    public void Validate_WhenReuseIsCurrentAndCompatible_DoesNotAddDiagnostics()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var feed = Node("Feeds-1", OctopusResourceKind.Feed, "Docker");
        var preview = Preview([feed], generatedAt);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 200;
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(
                feed,
                Match(200, OctopusResourceKind.Feed, "Docker", generatedAt.AddMinutes(-1)))
        ]);

        var result = _validator.Validate(Graph([feed]), Plan([feed]), conflicts, preview);

        result.Diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(2, 2, false)]
    [InlineData(2, 3, true)]
    [InlineData(2, 1, true)]
    [InlineData(0, 1, true)]
    [InlineData(null, 5, false)]
    public void Validate_ReusedTeamMembershipChangedSincePreview_IsStale(int? previewMemberCount, int currentMemberCount, bool expectStale)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var team = Node("Teams-1", OctopusResourceKind.Team, "Solar");
        var preview = Preview([team], generatedAt);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 200;
        preview.Resources.Single().DestinationMemberCount = previewMemberCount;
        var destination = new OctopusImportDestinationResource(200, 7, OctopusResourceKind.Team, "Solar", null, generatedAt.AddMinutes(-1)) { MemberCount = currentMemberCount };
        var conflicts = new OctopusImportConflictDiscoveryResult([Conflict(team, new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))]);

        var result = _validator.Validate(Graph([team]), Plan([team]), conflicts, preview);

        if (expectStale)
        {
            var diagnostic = result.Diagnostics.Single();
            diagnostic.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
            diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
            diagnostic.Message.ShouldContain($"now has {currentMemberCount}");
        }
        else
        {
            result.Diagnostics.ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData("members-a", "members-a", false)]   // same people → reuse is still what the preview showed
    [InlineData("members-a", "members-b", true)]    // a member was swapped: same count, different people
    [InlineData(null, "members-b", false)]          // plan produced before the fingerprint existed → count-only check
    public void Validate_ReusedTeamWithSameMemberCount_IsStaleWhenTheMembersThemselvesChanged(string previewFingerprint, string currentFingerprint, bool expectStale)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var team = Node("Teams-1", OctopusResourceKind.Team, "Solar");
        var preview = Preview([team], generatedAt);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 200;
        preview.Resources.Single().DestinationMemberCount = 1;
        preview.Resources.Single().DestinationMemberFingerprint = previewFingerprint;
        var destination = new OctopusImportDestinationResource(200, 7, OctopusResourceKind.Team, "Solar", null, generatedAt.AddMinutes(-1)) { MemberCount = 1, MemberFingerprint = currentFingerprint };
        var conflicts = new OctopusImportConflictDiscoveryResult([Conflict(team, new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))]);

        var result = _validator.Validate(Graph([team]), Plan([team]), conflicts, preview);

        if (expectStale)
        {
            var diagnostic = result.Diagnostics.Single();
            diagnostic.Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
            diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
            diagnostic.Message.ShouldContain("'Solar'");
            diagnostic.Message.ShouldContain("membership changed");
        }
        else
        {
            result.Diagnostics.ShouldBeEmpty();
        }
    }

    [Fact]
    public void Validate_ReusedTeamModifiedAndMembershipChanged_ReportsOneStaleBlocker()
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var team = Node("Teams-1", OctopusResourceKind.Team, "Solar");
        var preview = Preview([team], generatedAt);
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 200;
        preview.Resources.Single().DestinationMemberCount = 1;
        var destination = new OctopusImportDestinationResource(200, 7, OctopusResourceKind.Team, "Solar", null, generatedAt.AddMinutes(1)) { MemberCount = 4 };
        var conflicts = new OctopusImportConflictDiscoveryResult([Conflict(team, new OctopusImportDestinationMatch(destination, OctopusImportIdentityMatchKind.Name))]);

        var result = _validator.Validate(Graph([team]), Plan([team]), conflicts, preview);

        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.StalePreviewPlan);
    }

    [Fact]
    public void Validate_WhenConflictingProjectWasChangedToCreate_AddsRenameBlocker()
    {
        var project = Node("Projects-1", OctopusResourceKind.Project, "App");
        var preview = Preview([project]);
        var conflicts = new OctopusImportConflictDiscoveryResult(
        [
            Conflict(project, Match(300, OctopusResourceKind.Project, "App"))
        ]);

        var result = _validator.Validate(Graph([project]), Plan([project]), conflicts, preview);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject);
    }

    [Fact]
    public void Validate_WhenReleasePackageFeedCannotBeResolved_AddsPreflightBlocker()
    {
        var action = ReleaseAction("Feeds-Missing");
        var release = Release();
        var resources = new[] { action, release };

        var result = _validator.Validate(Graph(resources), Plan(resources), NoConflicts(), Preview(resources));

        var diagnostic = result.Diagnostics.Single();
        diagnostic.Code.ShouldBe(OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping);
        diagnostic.SourceId.ShouldBe(release.SourceId);
        result.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void Validate_WhenReleasePackageFeedWillBeCreated_PassesPreflight()
    {
        var feed = Node("Feeds-1", OctopusResourceKind.Feed, "Packages");
        var action = ReleaseAction(feed.SourceId);
        var release = Release();
        var resources = new[] { feed, action, release };

        var result = _validator.Validate(Graph(resources), Plan(resources), NoConflicts(), Preview(resources));

        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_PreservesRequiredInputMarkersFromPreview()
    {
        var variable = Node("Variables-Secret", OctopusResourceKind.Variable, "ApiKey");
        var preview = Preview([variable]);
        preview.Resources.Single().RequiredInputs.Add(new OctopusImportRequiredInputDto
        {
            InputKey = "required-secret-input:SensitiveVariableValue:variable-value:Value",
            Kind = OctopusImportRequiredInputKind.SensitiveVariableValue,
            SourceId = variable.SourceId,
            SourceType = variable.Kind.ToString(),
            Name = variable.Name,
            FieldName = "Value",
            ValueType = "Sensitive",
            HasSourceValue = true,
            IsRequired = true
        });
        preview.RequiredInputs = preview.Resources.SelectMany(r => r.RequiredInputs).ToList();

        var result = _validator.Validate(Graph([variable]), Plan([variable]), NoConflicts(), preview);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing);
        result.RequiredInputs.Single().InputKey.ShouldBe("required-secret-input:SensitiveVariableValue:variable-value:Value");
    }

    [Fact]
    public void Validate_WhenSensitiveRequiredInputWasRemovedFromPreview_RebuildsMarkerAndAddsBlocker()
    {
        var variable = Node(
            "variable-value",
            OctopusResourceKind.Variable,
            "ApiKey",
            new OctopusVariableDto
            {
                Id = "variable-value",
                Name = "ApiKey",
                Type = "Sensitive",
                IsSensitive = true,
                Value = "encrypted-source-value"
            });
        var preview = Preview([variable]);

        var result = _validator.Validate(Graph([variable]), Plan([variable]), NoConflicts(), preview);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing);
        result.RequiredInputs.Single().Kind.ShouldBe(OctopusImportRequiredInputKind.SensitiveVariableValue);
    }

    [Fact]
    public void Validate_SensitiveInputFromStalePreview_IsDroppedWhenTheValueIsNowDecryptable()
    {
        var variable = Node("Variables-1", OctopusResourceKind.Variable, "Db:Password", new OctopusVariableDto { Id = "Variables-1", Name = "Db:Password", Type = "Sensitive", Value = OctopusExportTestCipher.Encrypt("secret", "pw") });
        var stalePreview = Preview([variable]);
        stalePreview.RequiredInputs.Add(OctopusImportRequiredInputBuilder.ForSensitiveVariable("Variables-1", variable.GetSource<OctopusVariableDto>()));
        var validator = new OctopusImportPreviewValidator(new OctopusExportSecretsAccessor { Secrets = OctopusExportSecrets.FromPassword("pw") });

        var result = validator.Validate(Graph([variable]), Plan([variable]), NoConflicts(), stalePreview);

        result.HasBlockers.ShouldBeFalse();
        result.RequiredInputs.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_SensitiveVariableWithoutExportSecrets_StillBlocks()
    {
        var variable = Node("Variables-1", OctopusResourceKind.Variable, "Db:Password", new OctopusVariableDto { Id = "Variables-1", Name = "Db:Password", Type = "Sensitive", Value = OctopusExportTestCipher.Encrypt("secret", "pw") });

        var result = _validator.Validate(Graph([variable]), Plan([variable]), NoConflicts(), Preview([variable]));

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing);
    }

    [Theory]
    [InlineData("password", "password", new string[0])]
    [InlineData("none", "none", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    [InlineData("unreadable", "unreadable", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    [InlineData("password", "none", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    [InlineData("password", "unreadable", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    [InlineData("none", "password", new[] { OctopusImportPreviewDiagnosticCodes.SensitiveResolutionChanged })]
    [InlineData("unreadable", "password", new[] { OctopusImportPreviewDiagnosticCodes.SensitiveResolutionChanged })]
    [InlineData("notretained", "notretained", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    [InlineData("password", "notretained", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    [InlineData("notretained", "password", new[] { OctopusImportPreviewDiagnosticCodes.SensitiveResolutionChanged })]
    [InlineData("notretained", "none", new[] { OctopusImportPreviewDiagnosticCodes.RequiredSensitiveVariableInputMissing })]
    public void PreviewAndValidation_WithDifferentExportSecrets_OnlyAcceptWhenBothCouldDecrypt(string previewSecrets, string validationSecrets, string[] expectedValidationBlockers)
    {
        // Preview is built by the REAL planner with one secret state; validation re-runs with another
        // (e.g. the master key was fixed or broken in between). The import may only proceed when both
        // decrypted the value, and a stale preview must say why instead of passing on a mismatched plan.
        var variable = Node("Variables-1", OctopusResourceKind.Variable, "Db:Password", new OctopusVariableDto { Id = "Variables-1", Name = "Db:Password", IsSensitive = true, Type = "String", Value = OctopusExportTestCipher.Encrypt("secret", "pw") });
        var preview = new OctopusImportPreviewPlanner(new OctopusExportSecretsAccessor { Secrets = SecretsFor(previewSecrets) }).BuildPreviewPlan(Plan([variable]), NoConflicts());
        var validator = new OctopusImportPreviewValidator(new OctopusExportSecretsAccessor { Secrets = SecretsFor(validationSecrets) });

        var result = validator.Validate(Graph([variable]), Plan([variable]), NoConflicts(), preview);

        result.Diagnostics.Where(d => d.Severity == OctopusImportCompatibilitySeverity.Blocker).Select(d => d.Code).Distinct().ShouldBe(expectedValidationBlockers);
        (!preview.HasBlockers && !result.HasBlockers).ShouldBe(previewSecrets == "password" && validationSecrets == "password");
        result.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable).ShouldBe(validationSecrets is "unreadable" or "notretained");
        preview.Diagnostics.Any(d => d.Code == OctopusImportPreviewDiagnosticCodes.ExportPasswordUnavailable).ShouldBe(previewSecrets is "unreadable" or "notretained");
    }

    private static OctopusExportSecrets SecretsFor(string kind) => kind switch
    {
        "password" => OctopusExportSecrets.FromPassword("pw"),
        "unreadable" => OctopusExportSecrets.Unreadable,
        "notretained" => OctopusExportSecrets.NotRetained,
        _ => OctopusExportSecrets.None
    };

    [Fact]
    public void Validate_ResponsibleTeamMissingFromExport_BlocksBeforeConfirmation()
    {
        var action = Node("Actions-1", OctopusResourceKind.DeploymentAction, "Approve");
        var reference = Reference("Actions-1", OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Team, "Teams-404", OctopusResourceKind.Team, false);

        var result = _validator.Validate(Graph([action], [reference]), Plan([action]), NoConflicts(), Preview([action]));

        result.Diagnostics.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.MissingResponsibleTeam);
        result.HasBlockers.ShouldBeTrue();
    }

    [Fact]
    public void Validate_ResponsibleTeamPresentInExport_DoesNotBlock()
    {
        var action = Node("Actions-1", OctopusResourceKind.DeploymentAction, "Approve");
        var team = Node("Teams-1", OctopusResourceKind.Team, "Solar", new OctopusTeamDto { Id = "Teams-1", Name = "Solar" });
        var reference = Reference("Actions-1", OctopusResourceKind.DeploymentAction, OctopusResourceReferenceKind.Team, "Teams-1", OctopusResourceKind.Team, false);

        var result = _validator.Validate(Graph([action, team], [reference]), Plan([action, team]), NoConflicts(), Preview([action, team]));

        result.HasBlockers.ShouldBeFalse();
    }

    [Fact]
    public void Validate_ReusingASingleLibraryVariableSetMatch_IsAllowed()
    {
        var library = Node("LibraryVariableSets-1", OctopusResourceKind.LibraryVariableSet, "Shared", new OctopusLibraryVariableSetDto { Id = "LibraryVariableSets-1", Name = "Shared" });
        var preview = Preview([library], DateTimeOffset.UtcNow.AddMinutes(1));
        preview.Resources.Single().PreviewAction = OctopusImportPreviewAction.ReuseExisting;
        preview.Resources.Single().DestinationId = 31;

        var result = _validator.Validate(Graph([library]), Plan([library]), new OctopusImportConflictDiscoveryResult([Conflict(library, Match(31, OctopusResourceKind.LibraryVariableSet, "Shared"))]), preview);

        result.HasBlockers.ShouldBeFalse();
    }

    private static OctopusResourceGraph Graph(
        IReadOnlyList<OctopusResourceNode> resources,
        IReadOnlyList<OctopusResourceReference> references = null)
        => new(resources, references ?? [], [], []);

    private static OctopusImportDependencyPlan Plan(IReadOnlyList<OctopusResourceNode> resources)
        => new(resources, [], [], []);

    private static OctopusImportConflictDiscoveryResult NoConflicts()
        => new([]);

    private static OctopusImportPreviewPlanDto Preview(
        IReadOnlyList<OctopusResourceNode> resources,
        DateTimeOffset? generatedAt = null)
        => new()
        {
            GeneratedAt = generatedAt ?? DateTimeOffset.UtcNow,
            Resources = resources.Select(resource => new OctopusImportResourceResultDto
            {
                SourceId = resource.SourceId,
                SourceType = resource.Kind.ToString(),
                SourceName = resource.Name,
                PreviewAction = OctopusImportPreviewAction.Create,
                OutcomeState = OctopusImportResourceOutcomeState.Pending
            }).ToList()
        };

    private static OctopusImportResourceConflict Conflict(
        OctopusResourceNode source,
        params OctopusImportDestinationMatch[] matches)
        => new(source, matches);

    private static OctopusImportDestinationMatch Match(
        int destinationId,
        OctopusResourceKind kind,
        string name,
        DateTimeOffset? lastModifiedDate = null)
        => new(
            new OctopusImportDestinationResource(
                destinationId,
                7,
                kind,
                name,
                null,
                lastModifiedDate ?? DateTimeOffset.UtcNow),
            OctopusImportIdentityMatchKind.Name);

    private static OctopusResourceReference Reference(
        string fromSourceId,
        OctopusResourceKind fromKind,
        OctopusResourceReferenceKind referenceKind,
        string toSourceId,
        OctopusResourceKind? toKind,
        bool isRequired)
        => new(fromSourceId, fromKind, referenceKind, toSourceId, toKind, "Projects-1", isRequired, isRequired);

    private static OctopusResourceNode Node(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        object source = null)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            "Projects-1",
            null,
            false,
            source ?? new object());

    private static OctopusResourceNode ReleaseAction(string feedId)
        => Node(
            "Actions-1",
            OctopusResourceKind.DeploymentAction,
            "Deploy",
            new OctopusDeploymentActionDto
            {
                Id = "Actions-1",
                Name = "Deploy",
                Packages =
                [
                    new OctopusActionPackageDto
                    {
                        Name = "app",
                        FeedId = feedId
                    }
                ]
            });

    private static OctopusResourceNode Release()
        => Node(
            "Releases-1",
            OctopusResourceKind.Release,
            "1.0.0",
            new OctopusReleaseDto
            {
                Id = "Releases-1",
                ProjectId = "Projects-1",
                SelectedPackages =
                [
                    new OctopusSelectedPackageDto
                    {
                        ActionName = "Deploy",
                        PackageReferenceName = "app",
                        Version = "1.2.3"
                    }
                ]
            });
}
