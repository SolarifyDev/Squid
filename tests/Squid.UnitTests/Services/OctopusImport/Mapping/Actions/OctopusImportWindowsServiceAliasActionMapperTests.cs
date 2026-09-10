using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusImportWindowsServiceAliasActionMapperTests
{
    [Fact]
    public void DeployWindowsServiceAlias_MapsToSquidWindowsService()
    {
        var mapper = new OctopusImportDeployWindowsServiceActionMapper();
        var result = mapper.Map(Action("Octopus.DeployWindowsService"), Context());

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployWindowsService);
        result.Action.Properties.Single(property => property.PropertyName == "Squid.Action.WindowsService.ServiceName")
            .PropertyValue.ShouldBe("OrderWorker");
    }

    [Fact]
    public void WindowsServiceDeployAlias_MapsToSquidWindowsService()
    {
        var mapper = new OctopusImportWindowsServiceDeployActionMapper();
        var result = mapper.Map(Action("Octopus.WindowsServiceDeploy"), Context());

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.DeployWindowsService);
        result.Action.Properties.Single(property => property.PropertyName == "Squid.Action.WindowsService.ServiceName")
            .PropertyValue.ShouldBe("OrderWorker");
    }

    private static OctopusDeploymentActionDto Action(string actionType)
        => new()
        {
            Id = "Actions-Service",
            Name = "Deploy worker",
            ActionType = actionType,
            Properties =
            {
                ["Octopus.Action.WindowsService.ServiceName"] = "OrderWorker"
            }
        };

    private static OctopusImportActionMappingContext Context()
        => new(new OctopusImportIdMap(), 7);
}
