using System.Reflection;
using System.Text;
using Squid.Message.Models.Deployments.Variable;

namespace Squid.Core.Services.DeploymentExecution.Runtime.Bundles;

/// <summary>
/// Bash runtime bundle. Prepends:
/// <list type="number">
///   <item><description><c>#!/bin/bash</c> shebang</description></item>
///   <item><description>Squid scope exports (<c>SquidHome</c>, <c>SquidWorkDirectory</c>, <c>SquidServerTaskId</c>)</description></item>
///   <item><description>Sanitized <c>export</c> statements for every non-sensitive deployment variable</description></item>
///   <item><description>The embedded <c>squid-runtime.sh</c> helper functions</description></item>
///   <item><description>The user script body</description></item>
/// </list>
/// Sensitive variables are intentionally skipped so they never appear in <c>env</c>,
/// <c>ps</c>, or process-listing tools on the remote host.
/// </summary>
public class BashRuntimeBundle : IRuntimeBundle
{
    internal const string EnvSquidHome = "SquidHome";
    internal const string EnvSquidWorkDir = "SquidWorkDirectory";
    internal const string EnvSquidTaskId = "SquidServerTaskId";

    private const string ResourceFileName = "squid-runtime.sh";

    private static readonly Lazy<string> HelperSource = new(LoadHelperSource, isThreadSafe: true);

    public RuntimeBundleKind Kind => RuntimeBundleKind.Bash;

    /// <summary>The embedded <c>squid-runtime.sh</c> source, loaded once per process.</summary>
    public static string Helpers => HelperSource.Value;

    public string Wrap(RuntimeBundleWrapContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var sb = new StringBuilder();

        AppendHeader(sb);
        AppendSquidScopeExports(sb, context);
        AppendDeploymentVariableExports(sb, context.Variables);
        AppendHelpers(sb);
        AppendUserScript(sb, context.UserScriptBody);

        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb)
    {
        sb.AppendLine("#!/bin/bash");
    }

    private static void AppendSquidScopeExports(StringBuilder sb, RuntimeBundleWrapContext context)
    {
        // EscapeBashValue now returns a fully single-quoted bash literal —
        // do NOT wrap in additional quotes.
        sb.AppendLine($"export {EnvSquidHome}={EscapeBashValue(context.BaseDirectory)}");
        sb.AppendLine($"export {EnvSquidWorkDir}={EscapeBashValue(context.WorkDirectory)}");
        sb.AppendLine($"export {EnvSquidTaskId}={EscapeBashValue(context.ServerTaskId.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
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
            var quoted = EscapeBashValue(variable.Value ?? string.Empty);
            sb.AppendLine($"export {envName}={quoted}");
        }
    }

    private static void AppendHelpers(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("# --- squid-runtime.sh (embedded) ---");
        sb.AppendLine(Helpers.TrimEnd());
        sb.AppendLine("# --- end squid-runtime.sh ---");
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
    /// Returns a fully single-quoted POSIX bash literal of <paramref name="value"/>.
    /// Caller appends the result directly to the right-hand side of an assignment;
    /// no further quoting is needed (or safe).
    ///
    /// <para><b>P1-B.6 (Phase-5 follow-up to 2026-04-24 audit)</b>: pre-fix the
    /// escaper used double-quote wrapping with backslash escapes for <c>"</c>,
    /// <c>$</c>, <c>`</c>, <c>!</c>, and <c>\</c> — but did NOT escape literal
    /// newlines. A variable value containing <c>\n</c> terminated the
    /// <c>export VAR="..."</c> statement, the rest of the value parsed as the
    /// next bash command. Variable values come from operator config, output-
    /// variable capture, and other operator-controlled paths (some of which
    /// can include adversarial content) — confirmed shell-injection vector.</para>
    ///
    /// <para>Single-quote wrapping eliminates the entire class of escape-bypass
    /// bugs by removing all metacharacter interpretation. Inside single
    /// quotes, EVERY character is literal: <c>$</c>, <c>`</c>, <c>!</c>,
    /// <c>\</c>, <c>"</c>, <c>#</c>, newlines, all preserved verbatim. The
    /// only character that can't appear literally inside single quotes is
    /// <c>'</c> itself — handled via the four-character POSIX idiom
    /// <c>'\''</c> (close-quote, escaped-quote, reopen-quote): writing
    /// <c>a'b</c> as <c>'a'\''b'</c>.</para>
    ///
    /// <para>Pinned by <c>BashRuntimeBundleTests.EscapeBashValue_*</c> +
    /// <c>Wrap_VariableValueWithNewline_NoInjectionInExportLine</c>
    /// (regression for the actual exploit).</para>
    /// </summary>
    internal static string EscapeBashValue(string value)
    {
        var inner = (value ?? string.Empty).Replace("'", "'\\''");
        return "'" + inner + "'";
    }

    private static string LoadHelperSource()
    {
        var assembly = typeof(BashRuntimeBundle).GetTypeInfo().Assembly;
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
