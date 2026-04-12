using System.Reflection;
using System.Text;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Runtime.Bundles;

/// <summary>
/// PowerShell runtime bundle. Prepends Squid scope variables, deployment variable
/// exports, and the embedded <c>squid-runtime.ps1</c> helper functions to the user
/// script. Sensitive variables are skipped so they never appear in <c>Get-ChildItem Env:</c>.
/// </summary>
public class PowerShellRuntimeBundle : IRuntimeBundle
{
    internal const string EnvSquidHome = "SquidHome";
    internal const string EnvSquidWorkDir = "SquidWorkDirectory";
    internal const string EnvSquidTaskId = "SquidServerTaskId";

    private const string ResourceFileName = "squid-runtime.ps1";

    private static readonly Lazy<string> HelperSource = new(LoadHelperSource, isThreadSafe: true);

    public RuntimeBundleKind Kind => RuntimeBundleKind.PowerShell;

    /// <summary>The embedded <c>squid-runtime.ps1</c> source, loaded once per process.</summary>
    public static string Helpers => HelperSource.Value;

    public string Wrap(RuntimeBundleWrapContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sb = new StringBuilder();

        AppendSquidScopeExports(sb, context);
        AppendDeploymentVariableExports(sb, context.Variables);
        AppendHelpers(sb);
        AppendUserScript(sb, context.UserScriptBody);

        return sb.ToString();
    }

    private static void AppendSquidScopeExports(StringBuilder sb, RuntimeBundleWrapContext context)
    {
        sb.AppendLine($"$env:{EnvSquidHome} = '{EscapePowerShellSingleQuoted(context.BaseDirectory)}'");
        sb.AppendLine($"$env:{EnvSquidWorkDir} = '{EscapePowerShellSingleQuoted(context.WorkDirectory)}'");
        sb.AppendLine($"$env:{EnvSquidTaskId} = '{context.ServerTaskId}'");
    }

    private static void AppendDeploymentVariableExports(StringBuilder sb, IReadOnlyList<VariableDto> variables)
    {
        if (variables == null || variables.Count == 0) return;

        foreach (var variable in variables)
        {
            if (variable == null) continue;
            if (variable.IsSensitive) continue;
            if (string.IsNullOrEmpty(variable.Name)) continue;

            var envName = SanitizeEnvVarName(variable.Name);
            var escaped = EscapePowerShellSingleQuoted(variable.Value ?? string.Empty);
            sb.AppendLine($"$env:{envName} = '{escaped}'");
        }
    }

    private static void AppendHelpers(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("# --- squid-runtime.ps1 (embedded) ---");
        sb.AppendLine(Helpers.TrimEnd());
        sb.AppendLine("# --- end squid-runtime.ps1 ---");
        sb.AppendLine();
    }

    private static void AppendUserScript(StringBuilder sb, string userScriptBody)
    {
        sb.Append(userScriptBody ?? string.Empty);
    }

    internal static string SanitizeEnvVarName(string name)
    {
        var sb = new StringBuilder(name.Length);

        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }

        if (sb.Length > 0 && char.IsDigit(sb[0]))
            sb.Insert(0, '_');

        return sb.ToString();
    }

    /// <summary>
    /// Escapes a value for PowerShell single-quoted string literals. Single quotes
    /// are doubled (<c>'</c> → <c>''</c>); everything else is passed through because
    /// single-quoted strings in PowerShell don't interpolate.
    /// </summary>
    internal static string EscapePowerShellSingleQuoted(string value)
    {
        return (value ?? string.Empty).Replace("'", "''");
    }

    private static string LoadHelperSource()
    {
        var assembly = typeof(PowerShellRuntimeBundle).GetTypeInfo().Assembly;
        var resourceName = FindResourceName(assembly, ResourceFileName);

        if (resourceName == null)
            throw new InvalidOperationException($"Embedded resource not found: {ResourceFileName}");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource stream is null: {resourceName}");

        using var reader = new StreamReader(stream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static string FindResourceName(Assembly assembly, string fileName)
    {
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (name.EndsWith("." + fileName, StringComparison.Ordinal))
                return name;
        }

        return null;
    }
}
