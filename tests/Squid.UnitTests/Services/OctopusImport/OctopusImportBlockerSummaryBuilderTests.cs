using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportBlockerSummaryBuilderTests
{
    [Fact]
    public void Build_GroupsBlockersByCodeAndCountsAffectedResources()
    {
        var preview = new OctopusImportPreviewPlanDto
        {
            Diagnostics =
            [
                Diagnostic(
                    OctopusImportPreviewDiagnosticCodes.MissingTargetRole,
                    "Step 1 references a missing target role.",
                    "DeploymentStep",
                    "Steps-1")
            ],
            Resources =
            [
                new OctopusImportResourceResultDto
                {
                    SourceId = "Steps-1",
                    SourceType = "DeploymentStep",
                    SourceName = "Deploy",
                    Diagnostics =
                    [
                        Diagnostic(
                            OctopusImportPreviewDiagnosticCodes.MissingTargetRole,
                            "Step 1 references a missing target role.",
                            "DeploymentStep",
                            "Steps-1")
                    ]
                },
                new OctopusImportResourceResultDto
                {
                    SourceId = "Steps-2",
                    SourceType = "DeploymentStep",
                    SourceName = "Deploy API",
                    Diagnostics =
                    [
                        Diagnostic(
                            OctopusImportPreviewDiagnosticCodes.MissingTargetRole,
                            "Step 2 references a missing target role.",
                            "DeploymentStep",
                            "Steps-2")
                    ]
                },
                new OctopusImportResourceResultDto
                {
                    SourceId = "Variables-1",
                    SourceType = "Variable",
                    SourceName = "ApiKey",
                    Diagnostics =
                    [
                        new OctopusImportDiagnosticDto
                        {
                            Severity = OctopusImportCompatibilitySeverity.Warning,
                            Code = "warning-only",
                            Message = "This warning must not block import."
                        }
                    ]
                }
            ]
        };

        var summary = OctopusImportBlockerSummaryBuilder.Build(preview);

        summary.HasBlockers.ShouldBeTrue();
        summary.BlockerCount.ShouldBe(2);
        summary.AffectedResourceCount.ShouldBe(2);
        summary.BlockingReasons.Count.ShouldBe(1);
        summary.BlockingReasons.Single().Code.ShouldBe(OctopusImportPreviewDiagnosticCodes.MissingTargetRole);
        summary.BlockingReasons.Single().OccurrenceCount.ShouldBe(2);
        summary.BlockingReasons.Single().AffectedResourceCount.ShouldBe(2);
        summary.BlockingReasons.Single().Message.ShouldBe(
            "The following resources reference a deployment target that is not available in the destination: deployment step 'Deploy' and deployment step 'Deploy API'.");
    }

    [Fact]
    public void Build_DeduplicatesTheSameDiagnosticReportedAtMultipleLevels()
    {
        var diagnostic = Diagnostic(
            OctopusImportPreviewDiagnosticCodes.StalePreviewPlan,
            "The destination changed.",
            "Environment",
            "Environments-1");

        var summary = OctopusImportBlockerSummaryBuilder.Build(
            [diagnostic],
            [new OctopusImportResourceResultDto { Diagnostics = [diagnostic] }]);

        summary.BlockerCount.ShouldBe(1);
        summary.BlockingReasons.Single().OccurrenceCount.ShouldBe(1);
        summary.AffectedResourceCount.ShouldBe(1);
    }

    [Fact]
    public void Build_WithNoBlockersReturnsAnEmptySummary()
    {
        var summary = OctopusImportBlockerSummaryBuilder.Build(
            [
                new OctopusImportDiagnosticDto
                {
                    Severity = OctopusImportCompatibilitySeverity.Info,
                    Code = "info",
                    Message = "Informational diagnostic."
                }
            ]);

        summary.HasBlockers.ShouldBeFalse();
        summary.BlockerCount.ShouldBe(0);
        summary.AffectedResourceCount.ShouldBe(0);
        summary.BlockingReasons.ShouldBeEmpty();
    }

    [Fact]
    public void Build_UsesDirectUserFacingMessageForUnsupportedTeam()
    {
        var diagnostic = Diagnostic(
            OctopusImportConfirmationDiagnosticCodes.ResourceTypeUnsupported,
            "Octopus Team resources are not created by the confirmation orchestrator.",
            "Team",
            "Teams-1");
        diagnostic.ResourceName = "Solar";

        var summary = OctopusImportBlockerSummaryBuilder.Build([diagnostic]);

        summary.BlockerCount.ShouldBe(1);
        summary.AffectedResourceCount.ShouldBe(1);
        summary.BlockingReasons.Single().Message.ShouldBe(
            "The team 'Solar' cannot be imported because team permissions are not supported.");
    }

    [Fact]
    public void Build_SuppressesRollbackDiagnosticsWhenRootBlockerExists()
    {
        var rootBlocker = Diagnostic(
            OctopusImportConfirmationDiagnosticCodes.ResourceTypeUnsupported,
            "Team is not supported.",
            "Team",
            "Teams-1");
        rootBlocker.ResourceName = "Solar";

        var rollback = Diagnostic(
            OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack,
            "This resource was not completed because the import transaction was rolled back.",
            "Project",
            "Projects-1");
        rollback.ResourceName = "Next Chat";

        var summary = OctopusImportBlockerSummaryBuilder.Build(
            [
                rootBlocker,
                new OctopusImportDiagnosticDto
                {
                    Severity = OctopusImportCompatibilitySeverity.Blocker,
                    Code = OctopusImportConfirmationDiagnosticCodes.TransactionRolledBack,
                    Message = "The import transaction was rolled back."
                }
            ],
            [
                new OctopusImportResourceResultDto
                {
                    SourceId = "Teams-1",
                    SourceType = "Team",
                    SourceName = "Solar",
                    Diagnostics = [rootBlocker]
                },
                new OctopusImportResourceResultDto
                {
                    SourceId = "Projects-1",
                    SourceType = "Project",
                    SourceName = "Next Chat",
                    Diagnostics = [rollback]
                }
            ]);

        summary.BlockerCount.ShouldBe(1);
        summary.AffectedResourceCount.ShouldBe(1);
        summary.BlockingReasons.Count.ShouldBe(1);
        summary.BlockingReasons.Single().Code.ShouldBe(
            OctopusImportConfirmationDiagnosticCodes.ResourceTypeUnsupported);
    }

    [Fact]
    public void Build_UsesDirectMessageForExistingProjectConflict()
    {
        var diagnostic = Diagnostic(
            OctopusImportPreviewDiagnosticCodes.RenameRequiredForProject,
            "A project with the same name or slug already exists.",
            "Project",
            "Projects-1");
        diagnostic.ResourceName = "Next Chat";

        var summary = OctopusImportBlockerSummaryBuilder.Build([diagnostic]);

        summary.BlockingReasons.Single().Message.ShouldBe(
            "The project 'Next Chat' already exists in the destination and must be renamed before importing.");
    }

    [Fact]
    public void Build_UsesDirectMessageForMissingReleasePackageFeedMapping()
    {
        var diagnostic = Diagnostic(
            OctopusImportConfirmationDiagnosticCodes.MissingReleasePackageFeedMapping,
            "The release package feed has not been mapped.",
            "Release",
            "Releases-1");
        diagnostic.ResourceName = "0.1.0";

        var summary = OctopusImportBlockerSummaryBuilder.Build([diagnostic]);

        summary.BlockingReasons.Single().Message.ShouldBe(
            "The release '0.1.0' references a package feed that is not available in the destination.");
    }

    private static OctopusImportDiagnosticDto Diagnostic(
        string code,
        string message,
        string resourceType,
        string sourceId)
        => new()
        {
            Severity = OctopusImportCompatibilitySeverity.Blocker,
            Code = code,
            Message = message,
            ResourceType = resourceType,
            SourceId = sourceId
        };
}
