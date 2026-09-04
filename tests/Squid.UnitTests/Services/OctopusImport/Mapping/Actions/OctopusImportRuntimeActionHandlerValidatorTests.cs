using System.Linq;
using Squid.Core.Services.DeploymentExecution.Handlers;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;
using Squid.Message.Models.Deployments.Process;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusImportRuntimeActionHandlerValidatorTests
{
    [Fact]
    public void Validate_WhenEnabledActionHasRuntimeHandler_ReturnsNoDiagnostics()
    {
        var handler = Mock.Of<IActionHandler>();
        var registry = new Mock<IActionHandlerRegistry>();
        registry
            .Setup(r => r.Resolve(It.Is<DeploymentActionDto>(a =>
                a.Name == "Run script"
                && a.ActionType == SpecialVariables.ActionTypes.Script
                && a.Properties.Single().PropertyName == SpecialVariables.Action.ScriptBody
                && a.Properties.Single().PropertyValue == "echo hello"
                && a.Environments.SequenceEqual(new[] { 101 })
                && a.Channels.SequenceEqual(new[] { 201 }))))
            .Returns(handler);
        var validator = new OctopusImportRuntimeActionHandlerValidator(registry.Object);

        var diagnostics = validator.Validate(SourceAction(), new CreateOrUpdateDeploymentActionModel
        {
            Name = "Run script",
            ActionType = SpecialVariables.ActionTypes.Script,
            IsDisabled = false,
            Properties =
            [
                new ActionPropertyModel
                {
                    PropertyName = SpecialVariables.Action.ScriptBody,
                    PropertyValue = "echo hello"
                }
            ],
            Environments = [101],
            Channels = [201]
        });

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public void Validate_WhenEnabledActionHasNoRuntimeHandler_ReturnsBlocker()
    {
        var registry = new Mock<IActionHandlerRegistry>();
        registry.Setup(r => r.Resolve(It.IsAny<DeploymentActionDto>())).Returns((IActionHandler)null);
        var validator = new OctopusImportRuntimeActionHandlerValidator(registry.Object);

        var diagnostics = validator.Validate(SourceAction(), new CreateOrUpdateDeploymentActionModel
        {
            Name = "Run script",
            ActionType = "Squid.UnknownMappedAction"
        });

        diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        diagnostics.Single().Code.ShouldBe(OctopusImportActionMappingDiagnosticCodes.MissingRuntimeActionHandler);
        diagnostics.Single().SourceId.ShouldBe("Actions-1");
        diagnostics.Single().ResourceName.ShouldBe("Run script");
    }

    [Fact]
    public void Validate_WhenMappedActionIsDisabled_DoesNotAskRuntimeRegistry()
    {
        var registry = new Mock<IActionHandlerRegistry>();
        var validator = new OctopusImportRuntimeActionHandlerValidator(registry.Object);

        var diagnostics = validator.Validate(SourceAction(), new CreateOrUpdateDeploymentActionModel
        {
            Name = "Unsupported placeholder",
            ActionType = SpecialVariables.ActionTypes.Script,
            IsDisabled = true
        });

        diagnostics.ShouldBeEmpty();
        registry.Verify(r => r.Resolve(It.IsAny<DeploymentActionDto>()), Times.Never);
    }

    [Fact]
    public void Validate_WhenMappedActionIsSkipped_DoesNotAskRuntimeRegistry()
    {
        var registry = new Mock<IActionHandlerRegistry>();
        var validator = new OctopusImportRuntimeActionHandlerValidator(registry.Object);

        var diagnostics = validator.Validate(SourceAction(), null);

        diagnostics.ShouldBeEmpty();
        registry.Verify(r => r.Resolve(It.IsAny<DeploymentActionDto>()), Times.Never);
    }

    private static OctopusDeploymentActionDto SourceAction()
        => new()
        {
            Id = "Actions-1",
            Name = "Run script",
            ActionType = "Octopus.Script"
        };
}
