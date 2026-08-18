using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportDeploymentProcessMapperTests
{
    private readonly OctopusImportDeploymentProcessMapper _mapper = new();

    [Fact]
    public void MapToCreateStepCommands_UsesActionMapperRegistryForSupportedKubernetesActions()
    {
        var mapper = new OctopusImportDeploymentProcessMapper(new OctopusImportActionMapperRegistry(
            [
                new OctopusKubernetesDeployContainersActionMapper(),
                new OctopusKubernetesDeployIngressActionMapper()
            ]));
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Deploy Kubernetes resources",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-1",
                    Name = "Deploy containers",
                    ActionType = "Octopus.KubernetesDeployContainers",
                    Packages =
                    [
                        new OctopusActionPackageDto
                        {
                            Name = "web",
                            PackageId = "sjdistributor/web",
                            FeedId = "Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43"
                        }
                    ],
                    Properties =
                    {
                        ["Octopus.Action.KubernetesContainers.Namespace"] = "#{K8SNamespace}",
                        ["Octopus.Action.KubernetesContainers.Containers"] = """[{"Name":"web","FeedId":"Feeds-1083"}]"""
                    }
                },
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-2",
                    Name = "Deploy ingress",
                    ActionType = "Octopus.KubernetesDeployIngress",
                    Properties =
                    {
                        ["Octopus.Action.KubernetesContainers.IngressName"] = "web-ingress",
                        ["Octopus.Action.KubernetesContainers.IngressRules"] = """[{"host":"example.com","http":{"paths":[{"key":"/","value":"80","option":"web","option2":"Prefix"}]}}]"""
                    }
                }
            ]
        });
        var idMap = IdMap();
        idMap.AddReused(Resource("Feeds-1083-D4C63A85E7894C5D8C20D9297FEA1A43", OctopusResourceKind.Feed, "Docker Hub", new OctopusFeedDto()), 77);

        var result = mapper.MapToCreateStepCommands(Resource(process), idMap, 7);

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.ActionPropertyMappingDeferred);

        var actions = result.Steps.Single().CreateCommand.Step.Actions;
        actions[0].ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployContainers);
        actions[0].Properties.ShouldContain(p => p.PropertyName == "Squid.Action.KubernetesContainers.Containers"
                                                 && p.PropertyValue.Contains("\"PackageId\":\"sjdistributor/web\""));
        actions[1].ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployIngress);
        actions[1].Properties.ShouldContain(p => p.PropertyName == "Squid.Action.KubernetesContainers.IngressRules"
                                                 && p.PropertyValue.Contains("\"serviceName\":\"web\""));
    }

    [Fact]
    public void MapToCreateStepCommands_MapsStepOrderingFieldsActionsTargetRolesAndScopes()
    {
        var process = Process(
            new OctopusDeploymentStepDto
            {
                Id = "Steps-1",
                Name = "Deploy containers",
                Condition = "Success",
                StartTrigger = "StartAfterPrevious",
                PackageRequirement = "LetOctopusDecide",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-1",
                        Name = "Deploy web",
                        ActionType = "Octopus.KubernetesDeployContainers",
                        IsRequired = true,
                        Environments = ["Environments-1"],
                        ExcludedEnvironments = ["Environments-2"],
                        Channels = ["Channels-1"],
                        Properties =
                        {
                            ["Octopus.Action.TargetRoles"] = "aws-eks-us,web",
                            ["Octopus.Action.KubernetesContainers.Namespace"] = "#{K8SNamespace}"
                        }
                    }
                ]
            },
            new OctopusDeploymentStepDto
            {
                Id = "Steps-2",
                Name = "Ingress",
                StartTrigger = "StartWithPrevious",
                PackageRequirement = "AfterPackageAcquisition",
                Actions =
                [
                    new OctopusDeploymentActionDto
                    {
                        Id = "Actions-2",
                        Name = "Deploy ingress",
                        ActionType = "Octopus.KubernetesDeployIngress",
                        IsRequired = true
                    }
                ]
            });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.Steps.Count.ShouldBe(2);
        result.Steps[0].SourceStepId.ShouldBe("Steps-1");
        result.Steps[0].SourceIndex.ShouldBe(0);
        result.Steps[1].SourceStepId.ShouldBe("Steps-2");
        result.Steps[1].SourceIndex.ShouldBe(1);

        var firstStep = result.Steps[0].CreateCommand;
        firstStep.ProcessId.ShouldBe(500);
        firstStep.SpaceId.ShouldBe(7);
        firstStep.Step.Name.ShouldBe("Deploy containers");
        firstStep.Step.StepType.ShouldBe("Action");
        firstStep.Step.Condition.ShouldBe("Success");
        firstStep.Step.StartTrigger.ShouldBe("StartAfterPrevious");
        firstStep.Step.PackageRequirement.ShouldBe(SpecialVariables.PackageRequirements.LetSquidDecide);
        firstStep.Step.IsDisabled.ShouldBeFalse();
        firstStep.Step.IsRequired.ShouldBeTrue();
        firstStep.Step.Properties.Single(p => p.PropertyName == SpecialVariables.Step.TargetRoles).PropertyValue.ShouldBe("aws-eks-us,web");

        var firstAction = firstStep.Step.Actions.Single();
        firstAction.Name.ShouldBe("Deploy web");
        firstAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.KubernetesDeployContainers);
        firstAction.IsDisabled.ShouldBeFalse();
        firstAction.IsRequired.ShouldBeTrue();
        firstAction.Environments.ShouldBe([101]);
        firstAction.ExcludedEnvironments.ShouldBe([102]);
        firstAction.Channels.ShouldBe([201]);
        firstAction.Properties.ShouldBeEmpty();

        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.ActionPropertyMappingDeferred);
        result.Steps[0].Actions.Single().SourceActionId.ShouldBe("Actions-1");
        result.Steps[0].Actions.Single().ActionIndex.ShouldBe(0);
        result.Steps[1].CreateCommand.Step.StartTrigger.ShouldBe("StartWithPrevious");
        result.Steps[1].CreateCommand.Step.PackageRequirement.ShouldBe(SpecialVariables.PackageRequirements.AfterPackageAcquisition);
    }

    [Fact]
    public void MapToCreateStepCommands_MapsRunOnServerStepProperty()
    {
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-Server",
            Name = "Server script",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Server",
                    Name = "Run script",
                    ActionType = "Octopus.Script",
                    IsRequired = true,
                    Properties =
                    {
                        ["Octopus.Action.RunOnServer"] = "true"
                    }
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.Steps.Single().CreateCommand.Step.Actions.Single().ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
        result.Steps.Single().CreateCommand.Step.Properties.Single(p => p.PropertyName == SpecialVariables.Step.RunOnServer).PropertyValue.ShouldBe("true");
    }

    [Fact]
    public void MapToCreateStepCommands_WhenProcessMappingIsMissing_AddsBlocker()
    {
        var result = _mapper.MapToCreateStepCommands(
            Resource(Process()),
            new OctopusImportIdMap(),
            7);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingProcessMapping);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenEnvironmentOrChannelMappingIsMissing_AddsBlockersAndDropsMissingScope()
    {
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Deploy",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-1",
                    Name = "Deploy",
                    ActionType = "Octopus.Script",
                    Environments = ["Environments-Missing"],
                    ExcludedEnvironments = ["Environments-AlsoMissing"],
                    Channels = ["Channels-Missing"]
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        var action = result.Steps.Single().CreateCommand.Step.Actions.Single();
        action.Environments.ShouldBeEmpty();
        action.ExcludedEnvironments.ShouldBeEmpty();
        action.Channels.ShouldBeEmpty();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingEnvironmentMapping);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingExcludedEnvironmentMapping);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.MissingChannelMapping);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenActionHasUnsupportedTargetingOrCondition_AddsBlockers()
    {
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Deploy",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-1",
                    Name = "Deploy",
                    ActionType = "Octopus.Script",
                    WorkerPoolId = "WorkerPools-1",
                    EnvironmentsVariable = "#{EnvironmentIds}",
                    TenantTags = ["TenantTags/VIP"],
                    Condition = "Octopus.Action[Previous].Output.Flag == true"
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.WorkerPoolUnsupported);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.VariableScopedActionTargetUnsupported);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.TenantTagsUnsupported);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionCondition);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenActionTypeIsUnsupported_DisablesActionAndAddsBlocker()
    {
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Unsupported",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Unknown",
                    Name = "Community step",
                    ActionType = "Octopus.CustomCommunityStep"
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        var action = result.Steps.Single().CreateCommand.Step.Actions.Single();
        action.ActionType.ShouldBe("Octopus.CustomCommunityStep");
        action.IsDisabled.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionType);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenResourceIsNotDeploymentProcess_Throws()
    {
        Should.Throw<ArgumentException>(() => _mapper.MapToCreateStepCommands(
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

    private static OctopusDeploymentProcessDto Process(params OctopusDeploymentStepDto[] steps)
        => new()
        {
            Id = "deploymentprocess-Projects-1",
            OwnerId = "Projects-1",
            Version = 12,
            Steps = steps.ToList()
        };

    private static OctopusResourceNode Resource(OctopusDeploymentProcessDto process)
        => new(
            process.Id,
            null,
            OctopusResourceKind.DeploymentProcess,
            OctopusDocumentKind.DeploymentProcess,
            $"{process.Id}.json",
            process.OwnerId,
            null,
            false,
            process);

    private static OctopusImportIdMap IdMap()
    {
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("deploymentprocess-Projects-1", OctopusResourceKind.DeploymentProcess, "Process", new OctopusDeploymentProcessDto()), 500);
        idMap.AddReused(Resource("Environments-1", OctopusResourceKind.Environment, "Production", new OctopusEnvironmentDto()), 101);
        idMap.AddReused(Resource("Environments-2", OctopusResourceKind.Environment, "Staging", new OctopusEnvironmentDto()), 102);
        idMap.AddReused(Resource("Channels-1", OctopusResourceKind.Channel, "Default", new OctopusChannelDto()), 201);
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
            OctopusDocumentKind.DeploymentProcess,
            $"{sourceId}.json",
            null,
            null,
            false,
            source);
}
