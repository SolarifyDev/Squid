using System.Linq;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

public class OctopusScriptActionMapperTests
{
    private readonly OctopusScriptActionMapper _mapper = new();

    [Fact]
    public void Map_MapsSyntaxSourceBodyAndPackageReference()
    {
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-1",
            Name = "Run script",
            ActionType = "Octopus.Script",
            Properties =
            {
                ["Octopus.Action.Script.ScriptSource"] = "Inline",
                ["Octopus.Action.Script.Syntax"] = "PowerShell",
                ["Octopus.Action.Script.ScriptBody"] = "Write-Host #{Greeting}",
                ["Octopus.Action.Package.FeedId"] = "Feeds-1",
                ["Octopus.Action.Package.PackageId"] = "#{PackageId}",
                ["Octopus.Action.Package.PackageVersion"] = "#{PackageVersion}"
            }
        };

        var result = _mapper.Map(action, Context());

        result.HasBlockers.ShouldBeFalse();
        result.Action.ActionType.ShouldBe(SpecialVariables.ActionTypes.Script);
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptSource).PropertyValue.ShouldBe("Inline");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptSyntax).PropertyValue.ShouldBe("PowerShell");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptBody).PropertyValue.ShouldBe("Write-Host #{Greeting}");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageFeedId).PropertyValue.ShouldBe("301");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageId).PropertyValue.ShouldBe("#{PackageId}");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageVersion).PropertyValue.ShouldBe("#{PackageVersion}");
    }

    [Fact]
    public void Map_MapsPackageReferenceFromActionPackages()
    {
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-2",
            Name = "Run packaged script",
            ActionType = "Octopus.Script",
            Packages =
            [
                new OctopusActionPackageDto
                {
                    FeedId = "Feeds-1",
                    PackageId = "Acme.Tools",
                    Version = "1.2.3"
                }
            ]
        };

        var result = _mapper.Map(action, Context());

        result.HasBlockers.ShouldBeFalse();
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageFeedId).PropertyValue.ShouldBe("301");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageId).PropertyValue.ShouldBe("Acme.Tools");
        result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.PackageVersion).PropertyValue.ShouldBe("1.2.3");
    }

    [Fact]
    public void Map_WhenPackageFeedMappingIsMissing_AddsBlocker()
    {
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-3",
            Name = "Run script",
            ActionType = "Octopus.Script",
            Properties =
            {
                ["Octopus.Action.Package.FeedId"] = "Feeds-Missing",
                ["Octopus.Action.Package.PackageId"] = "Acme.Tools"
            }
        };

        var result = _mapper.Map(action, Context());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportActionMappingDiagnosticCodes.MissingPackageFeedMapping);
        result.Diagnostics.Single().Severity.ShouldBe(OctopusImportCompatibilitySeverity.Blocker);
        result.Action.Properties.ShouldNotContain(p => p.PropertyName == SpecialVariables.Action.PackageFeedId);
    }

    [Fact]
    public void Map_WhenSyntaxIsUnsupported_AddsBlocker()
    {
        var action = new OctopusDeploymentActionDto
        {
            Id = "Actions-4",
            Name = "Run script",
            ActionType = "Octopus.Script",
            Properties =
            {
                ["Octopus.Action.Script.Syntax"] = "Ruby"
            }
        };

        var result = _mapper.Map(action, Context());

        result.HasBlockers.ShouldBeTrue();
        result.Diagnostics.Single().Code.ShouldBe(OctopusImportActionMappingDiagnosticCodes.UnsupportedScriptSyntax);
        result.Action.Properties.ShouldNotContain(p => p.PropertyName == SpecialVariables.Action.ScriptSyntax);
    }

    private static OctopusImportActionMappingContext Context()
    {
        var idMap = new OctopusImportIdMap();
        idMap.AddReused(Resource("Feeds-1", OctopusResourceKind.Feed, "Built-in feed", new OctopusFeedDto()), 301);
        return new OctopusImportActionMappingContext(idMap, 7);
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
