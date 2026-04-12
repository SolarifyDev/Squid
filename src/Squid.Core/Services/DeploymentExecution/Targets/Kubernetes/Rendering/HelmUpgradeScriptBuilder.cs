using System.Text;
using Squid.Core.Services.DeploymentExecution.Infrastructure;
using Squid.Core.Services.DeploymentExecution.Intents;
using Squid.Core.Services.DeploymentExecution.Script.Files;
using Squid.Message.Models.Deployments.Execution;

namespace Squid.Core.Services.DeploymentExecution.Kubernetes.Rendering;

/// <summary>
/// Builds the <c>helm upgrade --install</c> shell script from a <see cref="HelmUpgradeIntent"/>.
/// Produces both Bash and PowerShell variants. Extracted as a static utility so both
/// <see cref="KubernetesApiIntentRenderer"/> and <see cref="KubernetesAgentIntentRenderer"/>
/// share a single implementation.
/// </summary>
internal static class HelmUpgradeScriptBuilder
{
    internal static string Build(HelmUpgradeIntent intent, ScriptSyntax syntax)
    {
        return syntax == ScriptSyntax.Bash
            ? BuildBash(intent)
            : BuildPowerShell(intent);
    }

    internal static Dictionary<string, byte[]> BuildFiles(HelmUpgradeIntent intent)
    {
        var files = new Dictionary<string, byte[]>(intent.ValuesFiles.Count);

        foreach (var file in intent.ValuesFiles)
            files[file.RelativePath] = file.Content;

        return files;
    }

    private static string BuildBash(HelmUpgradeIntent intent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/usr/bin/env bash");
        sb.AppendLine("set -euo pipefail");

        AppendBashRepoSetup(sb, intent.Repository);
        AppendBashHelmCommand(sb, intent);

        return sb.ToString();
    }

    private static void AppendBashRepoSetup(StringBuilder sb, HelmRepository? repo)
    {
        if (repo == null) return;

        var url = ShellEscapeHelper.EscapeBash(repo.Url);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(repo.Username) && !string.IsNullOrWhiteSpace(repo.Password))
        {
            var user = ShellEscapeHelper.EscapeBash(repo.Username!);
            var pass = ShellEscapeHelper.EscapeBash(repo.Password!);
            sb.AppendLine($"helm repo add \"{repo.Name}\" \"{url}\" --username \"{user}\" --password \"{pass}\" --force-update");
        }
        else
        {
            sb.AppendLine($"helm repo add \"{repo.Name}\" \"{url}\" --force-update");
        }

        sb.AppendLine($"helm repo update \"{repo.Name}\"");
    }

    private static void AppendBashHelmCommand(StringBuilder sb, HelmUpgradeIntent intent)
    {
        var helmExe = string.IsNullOrEmpty(intent.CustomHelmExecutable) ? "helm" : intent.CustomHelmExecutable;

        sb.AppendLine();
        sb.Append($"\"{helmExe}\" upgrade --install \"{intent.ReleaseName}\" \"{intent.ChartReference}\"");

        if (!string.IsNullOrEmpty(intent.Namespace))
            sb.Append($" --namespace \"{intent.Namespace}\"");

        if (intent.ResetValues)
            sb.Append(" --reset-values");

        if (intent.Wait)
            sb.Append(" --wait");

        if (intent.WaitForJobs)
            sb.Append(" --wait-for-jobs");

        if (!string.IsNullOrEmpty(intent.Timeout))
            sb.Append($" --timeout \"{intent.Timeout}\"");

        if (!string.IsNullOrEmpty(intent.ChartVersion))
            sb.Append($" --version \"{intent.ChartVersion}\"");

        AppendBashValuesFiles(sb, intent);
        AppendBashInlineValues(sb, intent);

        if (!string.IsNullOrEmpty(intent.AdditionalArgs))
            sb.Append($" {intent.AdditionalArgs}");

        sb.AppendLine();
    }

    private static void AppendBashValuesFiles(StringBuilder sb, HelmUpgradeIntent intent)
    {
        foreach (var file in intent.ValuesFiles)
            sb.Append($" --values \"./{file.RelativePath}\"");
    }

    private static void AppendBashInlineValues(StringBuilder sb, HelmUpgradeIntent intent)
    {
        foreach (var kvp in intent.InlineValues)
        {
            var escapedValue = kvp.Value.Replace("'", "'\\''", StringComparison.Ordinal);
            sb.Append($" --set \"{kvp.Key}='{escapedValue}'\"");
        }
    }

    private static string BuildPowerShell(HelmUpgradeIntent intent)
    {
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = \"Stop\"");

        AppendPowerShellRepoSetup(sb, intent.Repository);
        AppendPowerShellHelmCommand(sb, intent);

        return sb.ToString();
    }

    private static void AppendPowerShellRepoSetup(StringBuilder sb, HelmRepository? repo)
    {
        if (repo == null) return;

        var url = ShellEscapeHelper.EscapePowerShell(repo.Url);
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(repo.Username) && !string.IsNullOrWhiteSpace(repo.Password))
        {
            var user = ShellEscapeHelper.EscapePowerShell(repo.Username!);
            var pass = ShellEscapeHelper.EscapePowerShell(repo.Password!);
            sb.AppendLine($"& helm repo add \"{repo.Name}\" \"{url}\" --username \"{user}\" --password \"{pass}\" --force-update 2>$null");
        }
        else
        {
            sb.AppendLine($"& helm repo add \"{repo.Name}\" \"{url}\" --force-update 2>$null");
        }

        sb.AppendLine($"& helm repo update \"{repo.Name}\" 2>$null");
    }

    private static void AppendPowerShellHelmCommand(StringBuilder sb, HelmUpgradeIntent intent)
    {
        var helmExe = string.IsNullOrEmpty(intent.CustomHelmExecutable) ? "helm" : intent.CustomHelmExecutable;

        sb.AppendLine();
        sb.AppendLine($"$helmArgs = @(\"upgrade\", \"--install\", \"{intent.ReleaseName}\", \"{intent.ChartReference}\")");

        if (!string.IsNullOrEmpty(intent.Namespace))
            sb.AppendLine($"$helmArgs += \"--namespace\"; $helmArgs += \"{intent.Namespace}\"");

        if (intent.ResetValues)
            sb.AppendLine("$helmArgs += \"--reset-values\"");

        if (intent.Wait)
            sb.AppendLine("$helmArgs += \"--wait\"");

        if (intent.WaitForJobs)
            sb.AppendLine("$helmArgs += \"--wait-for-jobs\"");

        if (!string.IsNullOrEmpty(intent.Timeout))
            sb.AppendLine($"$helmArgs += \"--timeout\"; $helmArgs += \"{intent.Timeout}\"");

        if (!string.IsNullOrEmpty(intent.ChartVersion))
            sb.AppendLine($"$helmArgs += \"--version\"; $helmArgs += \"{intent.ChartVersion}\"");

        AppendPowerShellValuesFiles(sb, intent);
        AppendPowerShellInlineValues(sb, intent);

        if (!string.IsNullOrEmpty(intent.AdditionalArgs))
        {
            var escaped = ShellEscapeHelper.EscapePowerShell(intent.AdditionalArgs);
            sb.AppendLine($"$helmArgs += (\"{escaped}\" -split '\\s+(?=--?)') | ForEach-Object {{ $_.Trim() }} | Where-Object {{ $_ -ne '' }}");
        }

        sb.AppendLine($"& \"{helmExe}\" @helmArgs");
        sb.AppendLine("if ($LASTEXITCODE -ne 0) { throw \"Helm upgrade failed with exit code $LASTEXITCODE\" }");
    }

    private static void AppendPowerShellValuesFiles(StringBuilder sb, HelmUpgradeIntent intent)
    {
        foreach (var file in intent.ValuesFiles)
            sb.AppendLine($"$helmArgs += \"--values\"; $helmArgs += \".\\{file.RelativePath.Replace("/", "\\", StringComparison.Ordinal)}\"");
    }

    private static void AppendPowerShellInlineValues(StringBuilder sb, HelmUpgradeIntent intent)
    {
        foreach (var kvp in intent.InlineValues)
        {
            var escapedValue = ShellEscapeHelper.EscapePowerShell(kvp.Value);
            sb.AppendLine($"$helmArgs += \"--set\"; $helmArgs += \"{kvp.Key}={escapedValue}\"");
        }
    }
}
