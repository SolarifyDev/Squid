using System.Linq;
using System.Text.Json;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Enums;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportVariableMapperTests
{
    private readonly OctopusImportVariableMapper _mapper = new();

    [Fact]
    public void MapToCreateCommand_MapsProjectVariableSetVariablesPromptMetadataOrderingAndScopes()
    {
        var variableSet = VariableSet(
            new OctopusVariableDto
            {
                Id = "Variables-1",
                Name = "Namespace",
                Description = "Kubernetes namespace",
                Value = "next-chat",
                Type = "String",
                Prompt = new OctopusVariablePromptDto
                {
                    Label = "Namespace",
                    Description = "Enter a namespace",
                    Required = true
                },
                Scope =
                {
                    ["Environment"] = ["Environments-1"],
                    ["Channel"] = ["Channels-1"],
                    ["Action"] = ["Actions-1"],
                    ["Process"] = ["deploymentprocess-Projects-1"],
                    ["Role"] = ["aws-eks-us"]
                }
            },
            new OctopusVariableDto
            {
                Id = "Variables-2",
                Name = "ReplicaCount",
                Value = "3",
                Type = "Integer"
            });
        var idMap = IdMap();

        var result = _mapper.MapToCreateCommand(
            Resource(variableSet),
            idMap,
            7,
            "Project variables",
            "Imported from Octopus");

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.ShouldNotBeNull();
        result.UpdateCommand.ShouldBeNull();
        result.CreateCommand.Name.ShouldBe("Project variables");
        result.CreateCommand.Description.ShouldBe("Imported from Octopus");
        result.CreateCommand.OwnerId.ShouldBe(100);
        result.CreateCommand.OwnerType.ShouldBe(VariableSetOwnerType.Project);
        result.CreateCommand.SpaceId.ShouldBe(7);
        result.CreateCommand.Variables.Count.ShouldBe(2);

        var namespaceVariable = result.CreateCommand.Variables[0];
        namespaceVariable.Name.ShouldBe("Namespace");
        namespaceVariable.Value.ShouldBe("next-chat");
        namespaceVariable.Description.ShouldBe("Kubernetes namespace");
        namespaceVariable.Type.ShouldBe(VariableType.String);
        namespaceVariable.IsSensitive.ShouldBeFalse();
        namespaceVariable.SortOrder.ShouldBe(0);
        namespaceVariable.PromptLabel.ShouldBe("Namespace");
        namespaceVariable.PromptDescription.ShouldBe("Enter a namespace");
        namespaceVariable.PromptRequired.ShouldBeTrue();
        namespaceVariable.Scopes.Select(s => (s.ScopeType, s.ScopeValue)).ShouldBe([
            (VariableScopeType.Environment, "101"),
            (VariableScopeType.Channel, "201"),
            (VariableScopeType.Action, "301"),
            (VariableScopeType.Process, "401"),
            (VariableScopeType.Role, "aws-eks-us")
        ]);

        result.CreateCommand.Variables[1].Name.ShouldBe("ReplicaCount");
        result.CreateCommand.Variables[1].Type.ShouldBe(VariableType.Number);
        result.CreateCommand.Variables[1].SortOrder.ShouldBe(1);
    }

    [Fact]
    public void MapToUpdateCommand_TargetsDestinationVariableSet()
    {
        var result = _mapper.MapToUpdateCommand(
            Resource(VariableSet()),
            IdMap(),
            42,
            7);

        result.CreateCommand.ShouldBeNull();
        result.UpdateCommand.ShouldNotBeNull();
        result.UpdateCommand.Id.ShouldBe(42);
        result.UpdateCommand.OwnerId.ShouldBe(100);
        result.UpdateCommand.OwnerType.ShouldBe(VariableSetOwnerType.Project);
        result.UpdateCommand.SpaceId.ShouldBe(7);
    }

    [Fact]
    public void MapToCreateCommand_WhenVariableIsSensitive_OmitsSourceValueAndAddsWarning()
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = "Variables-Secret",
            Name = "ApiKey",
            Value = "encrypted-source-secret",
            Type = "Sensitive",
            IsSensitive = true
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.Variables[0].Type.ShouldBe(VariableType.Password);
        result.CreateCommand.Variables[0].IsSensitive.ShouldBeTrue();
        result.CreateCommand.Variables[0].Value.ShouldBe(string.Empty);
        var requiredInput = result.RequiredInputs.Single();
        requiredInput.InputKey.ShouldStartWith("required-secret-input:SensitiveVariableValue:");
        requiredInput.InputKey.ShouldEndWith(":Value");
        requiredInput.Kind.ShouldBe(OctopusImportRequiredInputKind.SensitiveVariableValue);
        requiredInput.SourceId.ShouldBe("Variables-Secret");
        requiredInput.SourceType.ShouldBe(OctopusResourceKind.Variable.ToString());
        requiredInput.Name.ShouldBe("ApiKey");
        requiredInput.FieldName.ShouldBe("Value");
        requiredInput.ValueType.ShouldBe("Sensitive");
        requiredInput.HasSourceValue.ShouldBeTrue();
        requiredInput.IsRequired.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportVariableMappingDiagnosticCodes.SensitiveValueOmitted);
        result.Diagnostics.All(d => d.Message.Contains("encrypted-source-secret", StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
        JsonSerializer.Serialize(result.RequiredInputs).ToLowerInvariant().ShouldNotContain("encrypted-source-secret");
    }

    [Fact]
    public void MapToCreateCommand_WhenSensitiveVariableHasScopes_RequiredInputPreservesSourceScopeMetadata()
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = "Variables-ScopedSecret",
            Name = "DatabasePassword",
            Value = "scoped-source-secret",
            IsSensitive = true,
            Scope =
            {
                ["Environment"] = ["Environments-2", "Environments-1"],
                ["Role"] = ["web"]
            }
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        var requiredInput = result.RequiredInputs.Single();
        requiredInput.HasSourceValue.ShouldBeTrue();
        requiredInput.SourceScopes["Environment"].ShouldBe(["Environments-1", "Environments-2"]);
        requiredInput.SourceScopes["Role"].ShouldBe(["web"]);
        result.CreateCommand.Variables.Single().Value.ShouldBe(string.Empty);
        JsonSerializer.Serialize(result).ToLowerInvariant().ShouldNotContain("scoped-source-secret");
    }

    [Theory]
    [InlineData("Certificate", VariableType.Certificate)]
    [InlineData("Boolean", VariableType.Boolean)]
    [InlineData("MultiLineText", VariableType.MultiLineText)]
    [InlineData("SelectList", VariableType.SelectList)]
    public void MapToCreateCommand_MapsSupportedVariableTypes(string octopusType, VariableType expectedType)
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = $"Variables-{octopusType}",
            Name = octopusType,
            Type = octopusType
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.Variables.Single().Type.ShouldBe(expectedType);
    }

    [Fact]
    public void MapToCreateCommand_WhenScopeMappingIsMissing_AddsBlockerAndDropsScope()
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = "Variables-Scoped",
            Name = "Scoped",
            Scope =
            {
                ["Environment"] = ["Environments-Missing"]
            }
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        result.CreateCommand.Variables.Single().Scopes.ShouldBeEmpty();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportVariableMappingDiagnosticCodes.MissingScopeMapping);
    }

    [Fact]
    public void MapToCreateCommand_WhenScopeTypeIsUnsupported_AddsBlockerAndDropsScope()
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = "Variables-Tenant",
            Name = "TenantScoped",
            Scope =
            {
                ["TenantTag"] = ["TenantTags/VIP"]
            }
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        result.CreateCommand.Variables.Single().Scopes.ShouldBeEmpty();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportVariableMappingDiagnosticCodes.UnsupportedScopeType);
    }

    [Fact]
    public void MapToCreateCommand_WhenVariableTypeIsUnsupported_AddsBlocker()
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = "Variables-Account",
            Name = "AwsAccount",
            Type = "AmazonWebServicesAccount"
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        result.CreateCommand.Variables.Single().Type.ShouldBe(VariableType.String);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportVariableMappingDiagnosticCodes.UnsupportedVariableType);
    }

    [Fact]
    public void MapToCreateCommand_WhenPromptDisplaySettingsArePresent_AddsWarning()
    {
        var variableSet = VariableSet(new OctopusVariableDto
        {
            Id = "Variables-Prompt",
            Name = "Prompted",
            Prompt = new OctopusVariablePromptDto
            {
                Label = "Choose",
                DisplaySettings = """{"Octopus.ControlType":"Select"}"""
            }
        });

        var result = _mapper.MapToCreateCommand(Resource(variableSet), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.CreateCommand.Variables.Single().PromptLabel.ShouldBe("Choose");
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportVariableMappingDiagnosticCodes.PromptDisplaySettingsOmitted);
    }

    [Fact]
    public void MapToCreateCommand_WhenProjectMappingIsMissing_AddsBlocker()
    {
        var result = _mapper.MapToCreateCommand(
            Resource(VariableSet()),
            new OctopusImportIdMap(),
            7);

        result.HasBlockers.ShouldBeTrue();
        result.CreateCommand.OwnerId.ShouldBe(0);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportVariableMappingDiagnosticCodes.MissingProjectMapping);
    }

    [Fact]
    public void MapToCreateCommand_WhenResourceIsNotVariableSet_Throws()
    {
        Should.Throw<ArgumentException>(() => _mapper.MapToCreateCommand(
            new OctopusResourceNode(
                "Projects-1",
                "Project",
                OctopusResourceKind.Project,
                OctopusDocumentKind.Project,
                "Projects-1.json",
                null,
                null,
                false,
                new OctopusProjectDto()),
            IdMap(),
            7));
    }

    private static OctopusVariableSetDto VariableSet(params OctopusVariableDto[] variables)
        => new()
        {
            Id = "variableset-Projects-1",
            OwnerId = "Projects-1",
            OwnerType = "Project",
            Version = 5,
            Variables = variables.ToList()
        };

    private static OctopusResourceNode Resource(OctopusVariableSetDto variableSet)
        => new(
            variableSet.Id,
            null,
            OctopusResourceKind.VariableSet,
            OctopusDocumentKind.VariableSet,
            $"{variableSet.Id}.json",
            variableSet.OwnerId,
            null,
            false,
            variableSet);

    private static OctopusImportIdMap IdMap()
    {
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("Projects-1", OctopusResourceKind.Project, "Project", new OctopusProjectDto()), 100);
        idMap.AddReused(Resource("Environments-1", OctopusResourceKind.Environment, "Production", new OctopusEnvironmentDto()), 101);
        idMap.AddReused(Resource("Channels-1", OctopusResourceKind.Channel, "Default", new OctopusChannelDto()), 201);
        idMap.AddReused(Resource("Actions-1", OctopusResourceKind.DeploymentAction, "Deploy", new OctopusDeploymentActionDto()), 301);
        idMap.AddReused(Resource("deploymentprocess-Projects-1", OctopusResourceKind.DeploymentProcess, "Process", new OctopusDeploymentProcessDto()), 401);
        return idMap;
    }

    private static OctopusResourceNode Resource(
        string sourceId,
        OctopusResourceKind kind,
        string name,
        object source)
        => new(
            sourceId,
            name,
            kind,
            OctopusDocumentKind.VariableSet,
            $"{sourceId}.json",
            null,
            null,
            false,
            source);
}
