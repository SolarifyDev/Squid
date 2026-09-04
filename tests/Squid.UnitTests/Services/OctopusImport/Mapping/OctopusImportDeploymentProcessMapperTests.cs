using System.Linq;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.OctopusImport.Mapping;

public class OctopusImportDeploymentProcessMapperTests
{
    private readonly OctopusImportDeploymentProcessMapper _mapper = new(new OctopusImportActionMapperRegistry(BuiltInActionMappers()));

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
        firstAction.Properties.Single(p => p.PropertyName == "Squid.Action.KubernetesContainers.Namespace").PropertyValue.ShouldBe("#{K8SNamespace}");

        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.ActionPropertyMappingDeferred);
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
    public void MapToCreateStepCommands_UsesRegisteredActionMappersForScriptAndManualActions()
    {
        var mapper = new OctopusImportDeploymentProcessMapper(
            new OctopusImportActionMapperRegistry([
                new OctopusScriptActionMapper(),
                new OctopusManualActionMapper()
            ]));
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Script and approval",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Script",
                    Name = "Run script",
                    ActionType = "Octopus.Script",
                    IsRequired = true,
                    Properties =
                    {
                        ["Octopus.Action.Script.Syntax"] = "Bash",
                        ["Octopus.Action.Script.ScriptBody"] = "echo #{Greeting}",
                        ["Octopus.Action.Package.FeedId"] = "Feeds-1",
                        ["Octopus.Action.Package.PackageId"] = "Acme.Tools"
                    }
                },
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Manual",
                    Name = "Approval",
                    ActionType = "Octopus.Manual",
                    IsRequired = true,
                    Properties =
                    {
                        ["Octopus.Action.Manual.Instructions"] = "Approve #{Release.Number}",
                        ["Octopus.Action.Manual.ResponsibleTeamIds"] = "Teams-1"
                    }
                }
            ]
        });

        var result = mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        var actions = result.Steps.Single().CreateCommand.Step.Actions;

        var scriptAction = actions.Single(a => a.Name == "Run script");
        scriptAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
        scriptAction.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptSyntax).PropertyValue.ShouldBe("Bash");
        scriptAction.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptBody).PropertyValue.ShouldBe("echo #{Greeting}");
        scriptAction.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageFeedId).PropertyValue.ShouldBe("301");
        scriptAction.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageId).PropertyValue.ShouldBe("Acme.Tools");

        var manualAction = actions.Single(a => a.Name == "Approval");
        manualAction.ActionType.ShouldBe(SpecialVariables.ActionTypes.Manual);
        manualAction.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ManualInstructions).PropertyValue.ShouldBe("Approve #{Release.Number}");
        manualAction.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ManualResponsibleTeamIds).PropertyValue.ShouldBe("401");
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
    public void MapToCreateStepCommands_WhenActionConditionMatchesStepCondition_DoesNotAddBlocker()
    {
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Deploy",
            Condition = "Success",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-1",
                    Name = "Deploy",
                    ActionType = "Octopus.Script",
                    Condition = "Success"
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportDeploymentProcessMappingDiagnosticCodes.UnsupportedActionCondition);
        result.Steps.Single().CreateCommand.Step.Condition.ShouldBe("Success");
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
        result.Diagnostics.Single(d => d.Code == OctopusImportDeploymentProcessMappingDiagnosticCodes.WorkerPoolUnsupported)
            .Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenActionHasWorkerPoolOnly_AddsWarningAndOmitsWorkerPool()
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
                    WorkerPoolId = "WorkerPools-1"
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        var action = result.Steps.Single().CreateCommand.Step.Actions.Single();
        action.WorkerPoolId.ShouldBeNull();
        var diagnostic = result.Diagnostics.Single(d => d.Code == OctopusImportDeploymentProcessMappingDiagnosticCodes.WorkerPoolUnsupported);
        diagnostic.Severity.ShouldBe(OctopusImportCompatibilitySeverity.Warning);
    }

    [Fact]
    public void MapToCreateStepCommands_MapsAllowlistedIisPropertiesAndAcknowledgesOmittedProperties()
    {
        const string secret = "iis-source-password";
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-IIS",
            Name = "Deploy IIS",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-IIS",
                    Name = "Deploy web",
                    ActionType = "Octopus.IIS",
                    Properties =
                    {
                        ["Octopus.Action.IISWebSite.WebSiteName"] = "Orders",
                        ["Octopus.Action.IISWebSite.ApplicationPoolPassword"] = secret,
                        ["Octopus.Action.IISWebSite.LegacyUnsupportedSetting"] = "legacy-value",
                        ["Octopus.Action.TargetRoles"] = "web"
                    }
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        var action = result.Steps.Single().CreateCommand.Step.Actions.Single();
        action.ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployToIISWebSite);
        action.Properties.Single(p => p.PropertyName == "Squid.Action.IISWebSite.WebSiteName").PropertyValue.ShouldBe("Orders");
        action.Properties.ShouldNotContain(p => p.PropertyName == "Squid.Action.IISWebSite.ApplicationPoolPassword");
        action.Properties.ShouldNotContain(p => p.PropertyName == "Squid.Action.IISWebSite.LegacyUnsupportedSetting");
        result.Steps.Single().CreateCommand.Step.Properties.Single(p => p.PropertyName == SpecialVariables.Step.TargetRoles).PropertyValue.ShouldBe("web");
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.SensitiveActionPropertyValueOmitted);
        result.Diagnostics.All(d => d.Message.Contains(secret, StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public void MapToCreateStepCommands_MapsAllowlistedWindowsServicePropertiesAndAcknowledgesOmittedProperties()
    {
        const string secret = "windows-service-source-password";
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-Service",
            Name = "Deploy service",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Service",
                    Name = "Deploy worker",
                    ActionType = "Octopus.WindowsService",
                    Properties =
                    {
                        ["Octopus.Action.WindowsService.ServiceName"] = "OrderWorker",
                        ["Octopus.Action.WindowsService.ExecutablePath"] = "OrderWorker.exe",
                        ["Octopus.Action.WindowsService.CustomAccountPassword"] = secret,
                        ["Octopus.Action.WindowsService.UnsupportedRecoveryPolicy"] = "restart"
                    }
                }
            ]
        });

        var result = _mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        var action = result.Steps.Single().CreateCommand.Step.Actions.Single();
        action.ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployWindowsService);
        action.Properties.Single(p => p.PropertyName == "Squid.Action.WindowsService.ServiceName").PropertyValue.ShouldBe("OrderWorker");
        action.Properties.Single(p => p.PropertyName == "Squid.Action.WindowsService.ExecutablePath").PropertyValue.ShouldBe("OrderWorker.exe");
        action.Properties.ShouldNotContain(p => p.PropertyName == "Squid.Action.WindowsService.CustomAccountPassword");
        action.Properties.ShouldNotContain(p => p.PropertyName == "Squid.Action.WindowsService.UnsupportedRecoveryPolicy");
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.ActionPropertiesOmitted);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.SensitiveActionPropertyValueOmitted);
        result.Diagnostics.All(d => d.Message.Contains(secret, StringComparison.OrdinalIgnoreCase)).ShouldBeFalse();
    }

    [Fact]
    public void MapToCreateStepCommands_WhenActionTypeIsUnsupported_AddsDisabledPlaceholder()
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

        result.HasBlockers.ShouldBeFalse();
        var action = result.Steps.Single().CreateCommand.Step.Actions.Single();
        action.ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
        action.IsDisabled.ShouldBeTrue();
        action.Properties.Single(p => p.PropertyName == OctopusImportActionMapperRegistry.PlaceholderSourceIdProperty).PropertyValue.ShouldBe("Actions-Unknown");
        action.Properties.Single(p => p.PropertyName == OctopusImportActionMapperRegistry.PlaceholderSourceActionTypeProperty).PropertyValue.ShouldBe("Octopus.CustomCommunityStep");
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedActionType);
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedActionPlaceholderCreated);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenEnabledMappedActionHasNoRuntimeHandler_AddsBlocker()
    {
        var mapper = new OctopusImportDeploymentProcessMapper(
            new OctopusImportActionMapperRegistry([new OctopusScriptActionMapper()]),
            new OctopusImportRuntimeActionHandlerValidator(new ActionHandlerRegistry([])));
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Script",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Script",
                    Name = "Run script",
                    ActionType = "Octopus.Script",
                    Properties =
                    {
                        ["Octopus.Action.Script.ScriptBody"] = "echo hello"
                    }
                }
            ]
        });

        var result = mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.MissingRuntimeActionHandler);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenEnabledMappedActionHasRuntimeHandler_DoesNotAddRuntimeBlocker()
    {
        var mapper = new OctopusImportDeploymentProcessMapper(
            new OctopusImportActionMapperRegistry([new OctopusScriptActionMapper()]),
            new OctopusImportRuntimeActionHandlerValidator(new ActionHandlerRegistry([RuntimeHandler(SpecialVariables.ActionTypes.Script)])));
        var process = Process(new OctopusDeploymentStepDto
        {
            Id = "Steps-1",
            Name = "Script",
            Actions =
            [
                new OctopusDeploymentActionDto
                {
                    Id = "Actions-Script",
                    Name = "Run script",
                    ActionType = "Octopus.Script",
                    Properties =
                    {
                        ["Octopus.Action.Script.ScriptBody"] = "echo hello"
                    }
                }
            ]
        });

        var result = mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.MissingRuntimeActionHandler);
    }

    [Fact]
    public void MapToCreateStepCommands_WhenUnsupportedActionCreatesDisabledPlaceholder_DoesNotRequireRuntimeHandler()
    {
        var mapper = new OctopusImportDeploymentProcessMapper(
            new OctopusImportActionMapperRegistry([]),
            new OctopusImportRuntimeActionHandlerValidator(new ActionHandlerRegistry([])));
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

        var result = mapper.MapToCreateStepCommands(Resource(process), IdMap(), 7);

        result.HasBlockers.ShouldBeFalse();
        result.Diagnostics.Select(d => d.Code).ShouldContain(OctopusImportActionMappingDiagnosticCodes.UnsupportedActionPlaceholderCreated);
        result.Diagnostics.Select(d => d.Code).ShouldNotContain(OctopusImportActionMappingDiagnosticCodes.MissingRuntimeActionHandler);
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
        idMap.AddReused(Resource("Feeds-1", OctopusResourceKind.Feed, "Built-in feed", new OctopusFeedDto()), 301);
        idMap.AddReused(Resource("Teams-1", OctopusResourceKind.Team, "Release approvers", new OctopusTeamDto()), 401);
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

    private static IOctopusImportActionMapper[] BuiltInActionMappers()
        =>
        [
            new OctopusKubernetesDeployContainersActionMapper(),
            new OctopusKubernetesDeployIngressActionMapper(),
            new OctopusScriptActionMapper(),
            new OctopusManualActionMapper(),
            new OctopusImportIisActionMapper(),
            new OctopusImportWindowsServiceActionMapper(),
            new OctopusImportDeployWindowsServiceActionMapper(),
            new OctopusImportWindowsServiceDeployActionMapper()
        ];

    private static IActionHandler RuntimeHandler(string actionType)
    {
        var handler = new Mock<IActionHandler>();
        handler.Setup(h => h.ActionType).Returns(actionType);
        handler.Setup(h => h.CanHandle(It.IsAny<DeploymentActionDto>()))
            .Returns<DeploymentActionDto>(a => string.Equals(a?.ActionType, actionType, StringComparison.OrdinalIgnoreCase));
        return handler.Object;
    }
}
