using Squid.Core.Services.Common;
using Squid.Message.Models.Deployments.Execution;
using Squid.Core.Services.DeploymentExecution.Script;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class CalamariPayloadBuilder : ICalamariPayloadBuilder
{
    private readonly IYamlNuGetPacker _yamlNuGetPacker;

    public CalamariPayloadBuilder(IYamlNuGetPacker yamlNuGetPacker)
    {
        _yamlNuGetPacker = yamlNuGetPacker;
    }

    public CalamariPayload Build(ScriptExecutionRequest request)
        => Build(request, ScriptSyntax.PowerShell);

    public CalamariPayload Build(ScriptExecutionRequest request, ScriptSyntax syntax)
    {
        var packageBytes = request.DeploymentFiles.Any()
            ? _yamlNuGetPacker.CreateNuGetPackageFromYamlBytes(request.DeploymentFiles.ToLegacyDictionary())
            : Array.Empty<byte>();

        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var templateName = syntax == ScriptSyntax.Bash ? "DeployByCalamari.sh" : "DeployByCalamari.ps1";

        return new CalamariPayload
        {
            PackageFileName = $"squid.{request.ReleaseVersion}.nupkg",
            PackageBytes = packageBytes,
            VariableBytes = variableBytes,
            SensitiveBytes = sensitiveBytes,
            SensitivePassword = password,
            TemplateBody = UtilService.GetEmbeddedScriptContent(templateName)
        };
    }
}
