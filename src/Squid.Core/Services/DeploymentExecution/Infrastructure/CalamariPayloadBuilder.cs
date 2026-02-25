using Squid.Core.Services.Common;

namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class CalamariPayloadBuilder : ICalamariPayloadBuilder
{
    private readonly IYamlNuGetPacker _yamlNuGetPacker;

    public CalamariPayloadBuilder(IYamlNuGetPacker yamlNuGetPacker)
    {
        _yamlNuGetPacker = yamlNuGetPacker;
    }

    public CalamariPayload Build(ScriptExecutionRequest request)
    {
        var packageBytes = request.Files?.Count > 0
            ? _yamlNuGetPacker.CreateNuGetPackageFromYamlBytes(request.Files)
            : Array.Empty<byte>();

        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        return new CalamariPayload
        {
            PackageFileName = $"squid.{request.ReleaseVersion}.nupkg",
            PackageBytes = packageBytes,
            VariableBytes = variableBytes,
            SensitiveBytes = sensitiveBytes,
            SensitivePassword = password,
            TemplateBody = UtilService.GetEmbeddedScriptContent("DeployByCalamari.ps1")
        };
    }
}
