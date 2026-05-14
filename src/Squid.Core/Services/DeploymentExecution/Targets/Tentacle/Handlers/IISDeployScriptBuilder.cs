using System.Reflection;
using System.Text;
using Squid.Message.Models.Deployments.Process;

namespace Squid.Core.Services.DeploymentExecution.Tentacle.Handlers;

/// <summary>
/// Builds the PowerShell script body for a <c>Squid.DeployToIISWebSite</c> action.
///
/// <para>The script has two segments concatenated at dispatch time:</para>
/// <list type="number">
///   <item>A <b>generated preamble</b> that populates the <c>$SquidParameters</c>
///         hashtable from the action's property values. Each property value is
///         escaped for PowerShell single-quote string literals
///         (<see cref="EscapeForPowerShellSingleQuote"/>) so that operator-provided
///         values containing apostrophes / <c>$</c> / backticks cannot break out
///         of the string.</item>
///   <item>A <b>verbatim body</b> read from the embedded resource
///         <c>Squid.Core.Resources.Deploy.IIS.DeployToIISWebSite.ps1</c>, which is
///         a 1:1 mirror of Octopus Calamari's IIS deploy script with namespace
///         renames applied. Never edit the body without updating the drift
///         detector at <c>IISDeployScriptDriftDetectorTests</c>.</item>
/// </list>
///
/// <para>Mirroring Octopus's behaviour, properties NOT supplied by the operator
/// are emitted as empty strings (<c>''</c>) in the hashtable — the script body's
/// <c>Is-DeploymentTypeDisabled</c> / null-check logic handles missing values
/// gracefully. We do NOT bake operator-friendly defaults here; defaults live in
/// the PS1 itself so Squid + Octopus remain behaviourally aligned.</para>
/// </summary>
internal static class IISDeployScriptBuilder
{
    private const string EmbeddedScriptName = "Squid.Core.Resources.Deploy.IIS.DeployToIISWebSite.ps1";

    /// <summary>
    /// The complete set of <c>Squid.Action.IISWebSite.*</c> property names the script body reads.
    /// Each entry on this list is emitted into the generated <c>$SquidParameters</c> preamble
    /// regardless of whether the operator supplied a value — missing values become empty strings.
    ///
    /// <para>Adding a new property: bump this list AND add the constant on
    /// <see cref="IISDeployProperties"/>. The drift detector tracks both sides.</para>
    /// </summary>
    internal static readonly IReadOnlyList<string> RecognisedProperties = new[]
    {
        // Sub-feature toggles
        IISDeployProperties.CreateOrUpdateWebSite,
        IISDeployProperties.WebApplicationCreateOrUpdate,
        IISDeployProperties.VirtualDirectoryCreateOrUpdate,

        // WebSite
        IISDeployProperties.WebSiteName,
        IISDeployProperties.ApplicationPoolName,
        IISDeployProperties.ApplicationPoolIdentityType,
        IISDeployProperties.ApplicationPoolUsername,
        IISDeployProperties.ApplicationPoolPassword,
        IISDeployProperties.ApplicationPoolFrameworkVersion,
        IISDeployProperties.Bindings,
        IISDeployProperties.ExistingBindings,
        IISDeployProperties.WebRoot,
        IISDeployProperties.EnableAnonymousAuthentication,
        IISDeployProperties.EnableBasicAuthentication,
        IISDeployProperties.EnableWindowsAuthentication,
        IISDeployProperties.StartApplicationPool,
        IISDeployProperties.StartWebSite,
        IISDeployProperties.MaxRetryFailures,
        IISDeployProperties.SleepBetweenRetryFailuresInSeconds,

        // WebApplication (dormant in Phase 1 — toggles default to false)
        IISDeployProperties.WebApplicationWebSiteName,
        IISDeployProperties.WebApplicationVirtualPath,
        IISDeployProperties.WebApplicationPhysicalPath,
        IISDeployProperties.WebApplicationApplicationPoolName,
        IISDeployProperties.WebApplicationApplicationPoolIdentityType,
        IISDeployProperties.WebApplicationApplicationPoolUsername,
        IISDeployProperties.WebApplicationApplicationPoolPassword,
        IISDeployProperties.WebApplicationApplicationPoolFrameworkVersion,

        // VirtualDirectory (dormant in Phase 1)
        IISDeployProperties.VirtualDirectoryWebSiteName,
        IISDeployProperties.VirtualDirectoryVirtualPath,
        IISDeployProperties.VirtualDirectoryPhysicalPath,

        // Custom script slots (Phase 5)
        IISDeployProperties.CustomScriptsPreDeploy,
        IISDeployProperties.CustomScriptsPostDeploy,
    };

    internal static string Build(DeploymentActionDto action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var preamble = BuildPreamble(action);
        var body = LoadEmbeddedScriptBody();

        return preamble + body;
    }

    /// <summary>
    /// Properties whose value is an operator-authored SCRIPT BODY (not data). Multi-line scripts
    /// must preserve newlines (PowerShell statement separator) and arbitrary apostrophes / dollar
    /// signs without breakage — single-quote single-line escape cannot guarantee that. We instead
    /// emit these via base64 round-trip, which is byte-safe at the cost of slight verbosity in
    /// the rendered preamble.
    /// </summary>
    private static readonly HashSet<string> ScriptContentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        IISDeployProperties.CustomScriptsPreDeploy,
        IISDeployProperties.CustomScriptsPostDeploy,
    };

    private static string BuildPreamble(DeploymentActionDto action)
    {
        var sb = new StringBuilder();

        sb.AppendLine("# ── BEGIN GENERATED PREAMBLE (Squid IISDeployScriptBuilder) ──");
        sb.AppendLine("# This hashtable is populated server-side from the action's property values;");
        sb.AppendLine("# data values are pre-escaped for PowerShell single-quote literals.");
        sb.AppendLine("# Script-body values are base64-encoded so newlines + apostrophes pass through");
        sb.AppendLine("# verbatim at runtime. Do not edit.");
        sb.AppendLine("$SquidParameters = @{}");

        foreach (var propertyName in RecognisedProperties)
        {
            var rawValue = ReadProperty(action, propertyName);

            if (ScriptContentProperties.Contains(propertyName))
            {
                EmitScriptContentAssignment(sb, propertyName, rawValue);
            }
            else
            {
                EmitDataAssignment(sb, propertyName, rawValue);
            }
        }

        sb.AppendLine("# ── END GENERATED PREAMBLE ──");
        sb.AppendLine();

        return sb.ToString();
    }

    private static void EmitDataAssignment(StringBuilder sb, string propertyName, string rawValue)
    {
        var escaped = EscapeForPowerShellSingleQuote(rawValue);
        sb.Append("$SquidParameters['");
        sb.Append(propertyName);
        sb.Append("'] = '");
        sb.Append(escaped);
        sb.AppendLine("'");
    }

    private static void EmitScriptContentAssignment(StringBuilder sb, string propertyName, string rawValue)
    {
        // Empty script-content stays empty (no decode needed at runtime — saves a few CPU cycles
        // and keeps the rendered preamble readable when the operator didn't set the slot).
        if (string.IsNullOrEmpty(rawValue))
        {
            sb.Append("$SquidParameters['");
            sb.Append(propertyName);
            sb.AppendLine("'] = ''");
            return;
        }

        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawValue));
        sb.Append("$SquidParameters['");
        sb.Append(propertyName);
        sb.Append("'] = [System.Text.Encoding]::UTF8.GetString([System.Convert]::FromBase64String('");
        sb.Append(base64);
        sb.AppendLine("'))");
    }

    private static string ReadProperty(DeploymentActionDto action, string propertyName)
    {
        var prop = action.Properties?
            .FirstOrDefault(p => string.Equals(p.PropertyName, propertyName, StringComparison.OrdinalIgnoreCase));

        return prop?.PropertyValue ?? string.Empty;
    }

    /// <summary>
    /// Escapes <paramref name="value"/> for a PowerShell single-quote string literal. In single-quoted
    /// strings, the only character that requires escaping is the apostrophe itself, which is doubled
    /// (<c>''</c>). Variable expansion, backticks, and dollar signs are inert inside single quotes.
    ///
    /// <para>Newlines (<c>\r\n</c>) inside the value would break the one-line <c>$SquidParameters['X'] =
    /// 'value'</c> assignment — Bindings JSON in particular is multi-line in source form. We replace
    /// <c>\r\n</c> / <c>\n</c> / <c>\r</c> with a single space so the value stays on one line. JSON
    /// itself doesn't care about whitespace between tokens, so <c>{"a":1, "b":2}</c> behaves
    /// identically to the multi-line form once <c>ConvertFrom-Json</c> parses it on the agent.</para>
    /// </summary>
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
        var assembly = typeof(IISDeployScriptBuilder).Assembly;

        using var stream = assembly.GetManifestResourceStream(EmbeddedScriptName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{EmbeddedScriptName}' not found in assembly '{assembly.GetName().Name}'. " +
                $"Verify Squid.Core.csproj has '<EmbeddedResource Include=\"Resources\\Deploy\\IIS\\*.ps1\" />' " +
                $"and the .ps1 file exists at src/Squid.Core/Resources/Deploy/IIS/DeployToIISWebSite.ps1.");

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
