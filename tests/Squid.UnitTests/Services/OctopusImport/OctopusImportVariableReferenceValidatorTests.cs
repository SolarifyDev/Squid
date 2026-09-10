using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport;

public class OctopusImportVariableReferenceValidatorTests
{
    private readonly OctopusImportVariableReferenceValidator _validator = new();

    [Fact]
    public void Validate_WhenReferencesHaveDefinitionsOrSquidSystemVariables_ReturnsNoDiagnostics()
    {
        var graph = Graph(
            VariableSet(
                Variable("Variables-1", "Namespace", "#{Environment}-app"),
                Variable("Variables-2", "Environment", "prod"),
                Variable("Variables-3", "LowerName", "##{EscapedMissing}")),
            Process(new OctopusDeploymentStepDto
            {
                Id = "Steps-1",
                Name = "Deploy",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-1",
                        Name = "Deploy containers",
                        Properties =
                        {
                            ["Octopus.Action.KubernetesContainers.Namespace"] = "#{Namespace | ToLower}",
                            ["Octopus.Action.KubernetesContainers.Annotation"] = "#{Squid.Environment.Name}"
                        }
                    }
                ]
            }));

        var result = _validator.Validate(graph);

        result.Diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WhenReferenceHasNoVariableDefinition_AddsBlocker()
    {
        var graph = Graph(
            VariableSet(Variable("Variables-1", "Namespace", "prod")),
            Process(new OctopusDeploymentStepDto
            {
                Id = "Steps-1",
                Name = "Deploy",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-1",
                        Name = "Deploy containers",
                        Properties =
                        {
                            ["Octopus.Action.KubernetesContainers.Replicas"] = "#{ReplicaCount}"
                        }
                    }
                ]
            }));

        var result = _validator.Validate(graph);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportVariableReferenceDiagnosticCodes.MissingVariableDefinition);
        result.Diagnostics.Single().SourceId.ShouldBe("Actions-1");
        result.Diagnostics.Single().ResourceType.ShouldBe(OctopusResourceKind.DeploymentAction.ToString());
        result.Diagnostics.Single().Message.ShouldContain("#{ReplicaCount}");
        result.Diagnostics.Single().Message.ShouldContain("Action.Properties.Octopus.Action.KubernetesContainers.Replicas");
    }

    [Fact]
    public void Validate_WhenReferenceDiffersOnlyByCase_AddsWarning()
    {
        var graph = Graph(
            VariableSet(Variable("Variables-1", "K8SNameSpace", "next-chat-prd")),
            Process(new OctopusDeploymentStepDto
            {
                Id = "Steps-1",
                Name = "Deploy",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-1",
                        Name = "Deploy containers",
                        Properties =
                        {
                            ["Octopus.Action.KubernetesContainers.Namespace"] = "#{K8SNamespace}"
                        }
                    }
                ]
            }));

        var result = _validator.Validate(graph);

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportVariableReferenceDiagnosticCodes.CaseOnlyVariableMismatch);
        result.Diagnostics.Single().Message.ShouldContain("#{K8SNamespace}");
        result.Diagnostics.Single().Message.ShouldContain("K8SNameSpace");
    }

    [Fact]
    public void Validate_WhenOctopusSystemVariableHasSquidEquivalent_AddsWarning()
    {
        var graph = Graph(
            VariableSet(Variable("Variables-1", "Namespace", "#{Octopus.Environment.Name}")),
            Process());

        var result = _validator.Validate(graph);

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportVariableReferenceDiagnosticCodes.SystemVariableEquivalent);
        result.Diagnostics.Single().Message.ShouldContain("#{Octopus.Environment.Name}");
        result.Diagnostics.Single().Message.ShouldContain($"#{{{SpecialVariables.Environment.Name}}}");
    }

    [Fact]
    public void Validate_WhenOctopusSystemVariableHasNoKnownEquivalent_AddsBlocker()
    {
        var graph = Graph(
            VariableSet(Variable("Variables-1", "Custom", "#{Octopus.Unknown.Value}")),
            Process());

        var result = _validator.Validate(graph);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportVariableReferenceDiagnosticCodes.UnsupportedOctopusSystemVariable);
    }

    [Fact]
    public void Validate_ExtractsReferencesFromConditionalsAndPackageProperties()
    {
        var graph = Graph(
            VariableSet(
                Variable("Variables-1", "PackageVersion", "1.2.3"),
                Variable("Variables-2", "ShouldDeploy", "true")),
            Process(new OctopusDeploymentStepDto
            {
                Id = "Steps-1",
                Name = "Deploy",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-1",
                        Name = "Deploy package",
                        Packages =
                        [
                            new OctopusActionPackageDto
                            {
                                Id = "Packages-1",
                                Version = "#{PackageVersion}",
                                Properties =
                                {
                                    ["Enabled"] = "#{if ShouldDeploy}true#{/if}"
                                }
                            }
                        ]
                    }
                ]
            }));

        var result = _validator.Validate(graph);

        result.Diagnostics.ShouldBeEmpty();
    }

    private static OctopusVariableDto Variable(string id, string name, string value)
        => new()
        {
            Id = id,
            Name = name,
            Value = value
        };

    private static OctopusVariableSetDto VariableSet(params OctopusVariableDto[] variables)
        => new()
        {
            Id = "variableset-Projects-1",
            OwnerId = "Projects-1",
            OwnerType = "Project",
            Variables = variables.ToList()
        };

    private static OctopusDeploymentProcessDto Process(params OctopusDeploymentStepDto[] steps)
        => new()
        {
            Id = "deploymentprocess-Projects-1",
            OwnerId = "Projects-1",
            Steps = steps.ToList()
        };

    private static OctopusResourceGraph Graph(
        OctopusVariableSetDto variableSet,
        OctopusDeploymentProcessDto process)
        => new(
            [
                new OctopusResourceNode(
                    variableSet.Id,
                    null,
                    OctopusResourceKind.VariableSet,
                    OctopusDocumentKind.VariableSet,
                    $"{variableSet.Id}.json",
                    variableSet.OwnerId,
                    null,
                    false,
                    variableSet),
                new OctopusResourceNode(
                    process.Id,
                    null,
                    OctopusResourceKind.DeploymentProcess,
                    OctopusDocumentKind.DeploymentProcess,
                    $"{process.Id}.json",
                    process.OwnerId,
                    null,
                    false,
                    process)
            ],
            [],
            [],
            []);
}
