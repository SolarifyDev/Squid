using System.Collections.Generic;
using System.Text.Json;
using Squid.Core.Services.OctopusImport.History;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;

namespace Squid.UnitTests.Services.OctopusImport.History;

public class OctopusImportDeploymentMachineNamesTests
{
    [Fact]
    public void RecordedNames_AlignWithTheIdsByPosition()
        => OctopusImportDeploymentMachineNames.Resolve(Deployment(["Machines-1", "Machines-9"], """["web-01","Machines-9"]"""), null).ShouldBe(["web-01", "Machines-9"]);

    [Fact]
    public void ExportedMachineName_WinsOverTheRecordedName()
    {
        var exported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["machines-1"] = "web-01 (exported)" };

        OctopusImportDeploymentMachineNames.Resolve(Deployment(["Machines-1", "Machines-2"], """["web-01","web-02"]"""), exported).ShouldBe(["web-01 (exported)", "web-02"]);
    }

    [Theory]
    [InlineData(null)]                         // an export document: no recorded names
    [InlineData("""["only-one"]""")]           // cannot be aligned with two ids
    [InlineData("\"web-01\"")]                 // not an array
    [InlineData("""[null,"  "]""")]            // blank entries
    public void UnusableRecordedNames_FallBackToUnknown(string recordedJson)
        => OctopusImportDeploymentMachineNames.Resolve(Deployment(["Machines-1", "Machines-2"], recordedJson), null).ShouldBe([null, null]);

    [Fact]
    public void NoMachines_NoNames()
        => OctopusImportDeploymentMachineNames.Resolve(new OctopusDeploymentDto { DeployedToMachineIds = null }, null).ShouldBeEmpty();

    private static OctopusDeploymentDto Deployment(List<string> machineIds, string recordedJson)
    {
        var deployment = new OctopusDeploymentDto { Id = "Deployments-1", DeployedToMachineIds = machineIds };

        if (recordedJson != null) deployment.ExtensionData[OctopusRestHistoryFetcher.DeployedToMachineNamesProperty] = JsonDocument.Parse(recordedJson).RootElement.Clone();

        return deployment;
    }
}
