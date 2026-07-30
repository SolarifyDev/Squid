using System.Reflection;
using System.Text;
using Squid.Message.Models.Deployments.Process;
using Squid.Message.Models.Deployments.Release;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;

internal static class WindowsServiceDeployScriptBuilder
{
    private const string EmbeddedScriptName = "Squid.Core.Resources.Deploy.WindowsService.DeployWindowsService.ps1";

    internal static readonly IReadOnlyList<string> RecognisedProperties = new[]
    {
        WindowsServiceDeployProperties.CreateOrUpdateService,
        WindowsServiceDeployProperties.ServiceName,
        WindowsServiceDeployProperties.DisplayName,
        WindowsServiceDeployProperties.Description,
        WindowsServiceDeployProperties.ExecutablePath,
        WindowsServiceDeployProperties.Arguments,
        WindowsServiceDeployProperties.ServiceAccount,
        WindowsServiceDeployProperties.CustomAccountName,
        WindowsServiceDeployProperties.CustomAccountPassword,
        WindowsServiceDeployProperties.StartMode,
        WindowsServiceDeployProperties.DesiredStatus,
        WindowsServiceDeployProperties.Dependencies,
        WindowsServiceDeployProperties.PackageSourcePath,
        WindowsServiceDeployProperties.PackageExtractTo,
        WindowsServiceDeployProperties.PackagePurgeBeforeExtract
    };

    private static readonly HashSet<string> MultilineProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        WindowsServiceDeployProperties.Description,
        WindowsServiceDeployProperties.Dependencies
    };

    internal static string Build(DeploymentActionDto action)
        => Build(action, Array.Empty<VariableDto>(), Array.Empty<SelectedPackageDto>());

    internal static string Build(
        DeploymentActionDto action,
        IReadOnlyList<VariableDto>? variables,
        IReadOnlyList<SelectedPackageDto>? selectedPackages)
    {
        ArgumentNullException.ThrowIfNull(action);

        var preamble = BuildPreamble(action, variables ?? Array.Empty<VariableDto>(), selectedPackages ?? Array.Empty<SelectedPackageDto>());
        var body = LoadEmbeddedScriptBody();

        return preamble + body;
    }

    private static string BuildPreamble(
        DeploymentActionDto action,
        IReadOnlyList<VariableDto> variables,
        IReadOnlyList<SelectedPackageDto> selectedPackages)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# BEGIN GENERATED PREAMBLE (Squid WindowsServiceDeployScriptBuilder)");
        sb.AppendLine("$SquidParameters = @{}");

        foreach (var propertyName in RecognisedProperties)
        {
            var rawValue = ReadProperty(action, propertyName);

            if (MultilineProperties.Contains(propertyName))
                EmitMultilineAssignment(sb, propertyName, rawValue);
            else
                EmitDataAssignment(sb, propertyName, rawValue);
        }

        EmitSquidVariablesHashtable(sb, variables);
        EmitSelectedPackages(sb, action, selectedPackages);

        sb.AppendLine("# END GENERATED PREAMBLE");
        sb.AppendLine();

        return sb.ToString();
    }

    private static void EmitDataAssignment(StringBuilder sb, string propertyName, string rawValue)
    {
        sb.Append("$SquidParameters['");
        sb.Append(propertyName);
        sb.Append("'] = '");
        sb.Append(EscapeForPowerShellSingleQuote(rawValue));
        sb.AppendLine("'");
    }

    private static void EmitMultilineAssignment(StringBuilder sb, string propertyName, string rawValue)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            EmitDataAssignment(sb, propertyName, string.Empty);
            return;
        }

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawValue));
        sb.Append("$SquidParameters['");
        sb.Append(propertyName);
        sb.Append("'] = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('");
        sb.Append(base64);
        sb.AppendLine("'))");
    }

    private static void EmitSquidVariablesHashtable(StringBuilder sb, IReadOnlyList<VariableDto> variables)
    {
        sb.AppendLine("$SquidVariables = @{}");

        foreach (var variable in variables)
        {
            if (string.IsNullOrEmpty(variable?.Name)) continue;

            sb.Append("$SquidVariables['");
            sb.Append(EscapeForPowerShellSingleQuote(variable.Name));
            sb.Append("'] = '");
            sb.Append(EscapeForPowerShellSingleQuote(variable.Value ?? string.Empty));
            sb.AppendLine("'");
        }
    }

    private static void EmitSelectedPackages(StringBuilder sb, DeploymentActionDto action, IReadOnlyList<SelectedPackageDto> selectedPackages)
    {
        sb.AppendLine("$SquidSelectedPackages = @()");

        foreach (var package in selectedPackages)
        {
            if (package == null) continue;

            sb.Append("$SquidSelectedPackages += @{ ActionName = '");
            sb.Append(EscapeForPowerShellSingleQuote(package.ActionName ?? string.Empty));
            sb.Append("'; PackageReferenceName = '");
            sb.Append(EscapeForPowerShellSingleQuote(package.PackageReferenceName ?? string.Empty));
            sb.Append("'; Version = '");
            sb.Append(EscapeForPowerShellSingleQuote(package.Version ?? string.Empty));
            sb.AppendLine("' }");
        }

        sb.AppendLine("$SquidSelectedPackage = $null");
        sb.Append("foreach ($package in $SquidSelectedPackages) { if ($package['ActionName'] -ieq '");
        sb.Append(EscapeForPowerShellSingleQuote(action.Name ?? string.Empty));
        sb.AppendLine("') { $SquidSelectedPackage = $package; break } }");
    }

    private static string ReadProperty(DeploymentActionDto action, string propertyName)
    {
        var prop = action.Properties?
            .FirstOrDefault(p => string.Equals(p.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase));

        return prop?.PropertyValue ?? string.Empty;
    }

    internal static string EscapeForPowerShellSingleQuote(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var collapsed = value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal);

        return collapsed.Replace("'", "''", StringComparison.Ordinal);
    }

    private static string LoadEmbeddedScriptBody()
    {
        var assembly = typeof(WindowsServiceDeployScriptBuilder).Assembly;

        using var stream = assembly.GetManifestResourceStream(EmbeddedScriptName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedScriptName}' not found in assembly '{assembly.GetName().Name}'. " +
                $"Verify Squid.Core.csproj has '<EmbeddedResource Include=\"Resources\\Deploy\\WindowsService\\*.ps1\" />' " +
                $"and the .ps1 file exists at src/Squid.Core/Resources/Deploy/WindowsService/DeployWindowsService.ps1.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
