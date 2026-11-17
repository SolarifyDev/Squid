using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Squid.Core.Services.OctopusImport;
using Squid.Core.Services.OctopusImport.Mapping.Actions;
using Squid.Core.Services.OctopusImport.Octopus;
using Squid.Core.Services.OctopusImport.Rest;
using Squid.Message.Constants;
using Squid.Message.Enums.OctopusImport;

namespace Squid.UnitTests.Services.OctopusImport.Mapping.Actions;

/// <summary>
/// An action property whose value the Octopus server withheld reaches the mappers as <c>null</c> plus the normaliser's marker.
/// It must be treated as a secret by every route (never written as an empty plain value, never inlined into a script).
/// </summary>
public class OctopusImportWithheldActionPropertyTests
{
    private const string Arguments = "Octopus.Action.WindowsService.Arguments";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowedPlainProperty_WithheldByTheServer_IsOmittedAsSensitive(bool withheld)
    {
        var action = RestAction("Octopus.WindowsService", withheld ? new { HasValue = true, NewValue = (string)null } : "--port 80");

        var result = new OctopusImportWindowsServiceActionMapper().Map(action, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 7));

        result.Action.Properties.Any(p => p.PropertyName == "Squid.Action.WindowsService.Arguments").ShouldBe(!withheld);
        result.Diagnostics.Any(d => d.Code == OctopusImportActionMappingDiagnosticCodes.SensitiveActionPropertyValueOmitted && d.Message.Contains(Arguments)).ShouldBe(withheld);
    }

    // The template definition says "plain" (or is out of date); the withheld value is still a secret: referenced, never inlined.
    [Fact]
    public void TemplateParameter_WithheldByTheServer_IsReferencedAsAVariable_NeverInlinedEmpty()
    {
        var action = RestAction("Octopus.Script", new { HasValue = true, NewValue = (string)null }, ("Octopus.Action.Script.ScriptSource", "Inline"), ("Octopus.Action.Script.Syntax", "PowerShell"), ("Octopus.Action.Script.ScriptBody", "Invoke $ApiKey"), ("Octopus.Action.Template.Id", "ActionTemplates-1"));
        var template = new OctopusActionTemplateDto { Id = "ActionTemplates-1", Name = "Notify", ActionType = "Octopus.Script", Parameters = [new OctopusActionTemplateParameterDto { Name = "ApiKey", DefaultValue = JsonSerializer.SerializeToElement("plain-default"), DisplaySettings = new() { ["Octopus.ControlType"] = "SingleLineText" } }] };

        var result = new OctopusScriptActionMapper().Map(action, new OctopusImportActionMappingContext(new OctopusImportIdMap(), 7, actionTemplates: new Dictionary<string, OctopusActionTemplateDto> { [template.Id] = template }));

        var script = result.Action.Properties.Single(p => p.PropertyName == SpecialVariables.Action.ScriptBody).PropertyValue;
        result.HasBlockers.ShouldBeFalse();
        script.ShouldContain("$OctopusParameters['ApiKey'] = __SquidOctopusValue '#{ApiKey | ToBase64}'");
        script.ShouldNotContain("$OctopusParameters['ApiKey'] = ''");
    }

    // The action exactly as the normaliser stages it: the secret property's value nulled, its name in the marker.
    private static OctopusDeploymentActionDto RestAction(string actionType, object secretValue, params (string Key, string Value)[] extra)
    {
        var name = actionType == "Octopus.Script" ? "ApiKey" : Arguments;
        var properties = new JsonObject { [name] = JsonSerializer.SerializeToNode(secretValue) };

        foreach (var (key, value) in extra) properties[key] = value;

        var process = new JsonObject { ["Id"] = "deploymentprocess-Projects-1", ["ProjectId"] = "Projects-1", ["Steps"] = new JsonArray(new JsonObject { ["Id"] = "s-1", ["Name"] = "Step", ["Properties"] = new JsonObject(), ["Actions"] = new JsonArray(new JsonObject { ["Id"] = "a-1", ["Name"] = "Install", ["ActionType"] = actionType, ["Properties"] = properties }) }) };
        var normalized = OctopusRestDocumentNormalizer.Normalize(OctopusResourceKind.DeploymentProcess, process);

        return normalized.Document.Deserialize<OctopusDeploymentProcessDto>().Steps[0].Actions[0];
    }
}
