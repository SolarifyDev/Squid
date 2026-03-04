namespace Squid.Core.Services.DeploymentExecution.Infrastructure;

public sealed class CalamariPayload
{
    public string PackageFileName { get; init; }
    public byte[] PackageBytes { get; init; }
    public byte[] VariableBytes { get; init; }
    public byte[] SensitiveBytes { get; init; }
    public string SensitivePassword { get; init; }
    public string TemplateBody { get; init; }

    public string FillTemplate(string packagePath, string variablePath, string sensitivePath)
        => TemplateBody
            .Replace("{{PackageFilePath}}", packagePath, StringComparison.Ordinal)
            .Replace("{{VariableFilePath}}", variablePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariableFile}}", string.IsNullOrEmpty(SensitivePassword) ? string.Empty : sensitivePath, StringComparison.Ordinal)
            .Replace("{{SensitiveVariablePassword}}", SensitivePassword, StringComparison.Ordinal);
}
