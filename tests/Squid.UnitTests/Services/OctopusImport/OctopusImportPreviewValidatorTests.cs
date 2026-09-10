using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
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

        result.Diagnostics.ShouldBeEmpty();
        result.RequiredInputs.Single().InputKey.ShouldBe("required-secret-input:SensitiveVariableValue:variable-value:Value");
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
        string name)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.Project,
            $"{sourceId}.json",
            "Projects-1",
            null,
            false,
            new object());
}
