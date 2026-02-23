using Squid.Core.Services.Common;
using Squid.Core.Settings.GithubPackage;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes;

public sealed class CalamariPayloadBuilder : ICalamariPayloadBuilder
{
    private readonly IYamlNuGetPacker _yamlNuGetPacker;
    private readonly CalamariGithubPackageSetting _calamariSetting;

    public CalamariPayloadBuilder(IYamlNuGetPacker yamlNuGetPacker, CalamariGithubPackageSetting calamariSetting)
    {
        _yamlNuGetPacker = yamlNuGetPacker;
        _calamariSetting = calamariSetting;
    }

    public string ResolvedVersion => _calamariSetting.ResolvedVersion;

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
