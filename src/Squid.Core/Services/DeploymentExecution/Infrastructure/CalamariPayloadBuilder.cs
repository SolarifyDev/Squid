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
        ArgumentNullException.ThrowIfNull(request);

        if (request.PayloadKind == PayloadKind.PackageArchive)
            return BuildPackageArchive(request, syntax);

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

    private static CalamariPayload BuildPackageArchive(ScriptExecutionRequest request, ScriptSyntax syntax)
    {
        var pkg = request.PackageReferences?.FirstOrDefault()
            ?? throw new InvalidOperationException("PackageArchive payload requires PackageReferences.");

        if (string.IsNullOrWhiteSpace(pkg.LocalPath) || !File.Exists(pkg.LocalPath))
            throw new InvalidOperationException($"Package archive path is missing or does not exist: '{pkg.LocalPath}'.");

        var packageBytes = File.ReadAllBytes(pkg.LocalPath);
        var (variableBytes, sensitiveBytes, password) =
            ScriptExecutionHelper.CreateVariableFileContents(request.Variables);

        var templateName = syntax == ScriptSyntax.Bash ? "DeployPackageByCalamari.sh" : "DeployPackageByCalamari.ps1";

        return new CalamariPayload
        {
            PackageFileName = Path.GetFileName(pkg.LocalPath),
            PackageBytes = packageBytes,
            VariableBytes = variableBytes,
            SensitiveBytes = sensitiveBytes,
            SensitivePassword = password,
            TemplateBody = UtilService.GetEmbeddedScriptContent(templateName)
        };
    }
}
