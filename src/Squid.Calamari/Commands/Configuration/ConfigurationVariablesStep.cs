using System.Xml;
using Squid.Calamari.Commands.Common;
using Squid.Calamari.Pipeline;
using Squid.Calamari.Variables;

namespace Squid.Calamari.Commands.Configuration;

/// <summary>
/// Wire-contract constants for the .NET Configuration Variables feature.
/// Public so cross-project drift tests can pin the contract without
/// InternalsVisibleTo pollution.
///
/// <para><b>Canonical vs Legacy</b>: top-level constants are the
/// handler-agnostic canonical wire literals — preferred for new handlers.
/// The nested <see cref="Legacy"/> class holds the IIS-prefixed names that
/// the IIS handler's PS1 script + existing operator deployments emit.
/// <see cref="ConfigurationVariablesStep"/> reads canonical first, falls
/// back to legacy.</para>
/// </summary>
public static class ConfigurationVariablesVariableNames
{
    /// <summary>Canonical, handler-agnostic Enabled toggle.</summary>
    public const string Enabled = "Squid.Action.ConfigurationVariables.Enabled";

    /// <summary>
    /// When True, XML parse failures are warned + skipped instead of throwing.
    /// Shared package-level toggle matching Octopus's
    /// <c>Octopus.Action.Package.IgnoreVariableReplacementErrors</c>.
    /// </summary>
    public const string IgnoreVariableReplacementErrors = "Squid.Action.Package.IgnoreVariableReplacementErrors";

    /// <summary>
    /// Legacy IIS-handler-specific wire literals. Existing operator deployments
    /// + the IIS PS1 script still emit these.
    /// <see cref="ConfigurationVariablesStep"/> falls back to these when the
    /// canonical literals above are not set.
    /// </summary>
    public static class Legacy
    {
        public const string Enabled = "Squid.Action.IISWebSite.ConfigurationVariables.Enabled";
    }
}

/// <summary>
/// Rewrites matching entries in <c>*.config</c> files from the deployment
/// VariableSet. Mirrors Octopus's ConfigurationVariables behaviour and the
/// IIS deploy script's <c>Update-IISConfigurationVariables</c>.
///
/// <para>Matches:
/// <list type="bullet">
///   <item><c>//appSettings/add[@key=...]</c> — writes <c>value</c></item>
///   <item><c>//connectionStrings/add[@name=...]</c> — writes <c>connectionString</c></item>
///   <item><c>//applicationSettings//setting[@name=...]</c> — writes/creates <c>value</c> child</item>
/// </list>
/// XPath uses local-name() so namespaced web.config files still match.</para>
///
/// <para>XML parse failure policy: throw by default; when
/// <see cref="ConfigurationVariablesVariableNames.IgnoreVariableReplacementErrors"/>
/// is True, warn + skip the broken file and continue.</para>
/// </summary>
internal sealed class ConfigurationVariablesStep : ExecutionStep<RunScriptCommandContext>
{
    public const string StepName = "ConfigurationVariables";

    // local-name() XPath keeps matching when <configuration xmlns="..."> is present.
    private static readonly string AppSettingsAddXPath =
        "//*[local-name()='appSettings']/*[local-name()='add'][@key]";
    private static readonly string ConnectionStringsAddXPath =
        "//*[local-name()='connectionStrings']/*[local-name()='add'][@name]";
    private static readonly string ApplicationSettingsXPath =
        "//*[local-name()='applicationSettings']//*[local-name()='setting'][@name]";

    public override bool IsEnabled(RunScriptCommandContext context)
    {
        if (context.Variables is null) return false;
        // Canonical first, legacy fallback.
        var raw = context.Variables.Get(ConfigurationVariablesVariableNames.Enabled)
                  ?? context.Variables.Get(ConfigurationVariablesVariableNames.Legacy.Enabled);
        return string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase);
    }

    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                "Working directory has not been initialized — ConfigurationVariablesStep must run after ResolveWorkingDirectoryStep.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                "Variables have not been loaded — ConfigurationVariablesStep must run after LoadVariablesFromFilesStep.");

        if (!Directory.Exists(context.WorkingDirectory))
        {
            Console.WriteLine(
                $"ConfigurationVariables: working dir '{context.WorkingDirectory}' does not exist; skipping.");
            context.StepOutcomes.Add(StepOutcome.Skipped(StepName, "Working directory does not exist") with { DurationMs = sw.ElapsedMilliseconds });
            return Task.CompletedTask;
        }

        var ignoreErrors = string.Equals(
            context.Variables.Get(ConfigurationVariablesVariableNames.IgnoreVariableReplacementErrors),
            "True",
            StringComparison.OrdinalIgnoreCase);

        var filesProcessed = 0;
        var filesFailed = 0;
        var replacements = 0;

        foreach (var configPath in EnumerateConfigFiles(context.WorkingDirectory))
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var count = RewriteConfigFile(configPath, context.Variables);
                filesProcessed++;
                replacements += count;
            }
            catch (XmlException ex)
            {
                filesFailed++;
                var msg =
                    $"ConfigurationVariables: failed to parse '{configPath}' as XML: {ex.Message}";
                if (ignoreErrors)
                {
                    Console.Error.WriteLine(
                        $"::warning::{msg} (IgnoreVariableReplacementErrors=True — skipping).");
                    continue;
                }

                throw new InvalidOperationException(
                    $"{msg}. Set {ConfigurationVariablesVariableNames.IgnoreVariableReplacementErrors}=True to log+skip instead of failing the deploy.",
                    ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                filesFailed++;
                Console.Error.WriteLine(
                    $"::warning::ConfigurationVariables: failed to process '{configPath}' — {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(
            $"ConfigurationVariables: processed {filesProcessed} file(s), {filesFailed} failure(s), {replacements} replacement(s).");

        context.StepOutcomes.Add(StepOutcome.Success(StepName, new Dictionary<string, long>
        {
            ["FilesProcessed"] = filesProcessed,
            ["FilesFailed"] = filesFailed,
            ["Replacements"] = replacements
        }) with { DurationMs = sw.ElapsedMilliseconds });

        return Task.CompletedTask;
    }

    private static int RewriteConfigFile(string path, VariableSet variables)
    {
        // Preserve BOM on round-trip; operators' Visual Studio web.config files
        // commonly ship with a UTF-8 BOM.
        var (content, encoding) = EncodingPreservingFileIO.ReadAllTextPreservingEncoding(path);

        var doc = new XmlDocument { PreserveWhitespace = true };
        try
        {
            doc.LoadXml(content);
        }
        catch (XmlException)
        {
            // Re-throw so the caller can apply ignore-errors policy.
            throw;
        }

        var modified = 0;

        foreach (XmlElement node in doc.SelectNodes(AppSettingsAddXPath)!)
        {
            var key = node.GetAttribute("key");
            if (string.IsNullOrEmpty(key) || !variables.Contains(key)) continue;

            var value = variables.Get(key) ?? string.Empty;
            if (node.GetAttribute("value") == value) continue;

            node.SetAttribute("value", value);
            Console.WriteLine($"ConfigurationVariables: appSettings/{key} replaced in '{path}'.");
            modified++;
        }

        foreach (XmlElement node in doc.SelectNodes(ConnectionStringsAddXPath)!)
        {
            var name = node.GetAttribute("name");
            if (string.IsNullOrEmpty(name) || !variables.Contains(name)) continue;

            var value = variables.Get(name) ?? string.Empty;
            if (node.GetAttribute("connectionString") == value) continue;

            node.SetAttribute("connectionString", value);
            Console.WriteLine($"ConfigurationVariables: connectionStrings/{name} replaced in '{path}'.");
            modified++;
        }

        foreach (XmlElement node in doc.SelectNodes(ApplicationSettingsXPath)!)
        {
            var name = node.GetAttribute("name");
            if (string.IsNullOrEmpty(name) || !variables.Contains(name)) continue;

            var value = variables.Get(name) ?? string.Empty;
            var valueNode = node.SelectSingleNode("*[local-name()='value']") as XmlElement;
            if (valueNode is null)
            {
                // Octopus parity: create the <value> child when missing.
                valueNode = doc.CreateElement("value", node.NamespaceURI);
                node.AppendChild(valueNode);
            }

            if (valueNode.InnerText == value) continue;

            valueNode.InnerText = value;
            Console.WriteLine($"ConfigurationVariables: applicationSettings/{name} replaced in '{path}'.");
            modified++;
        }

        if (modified > 0)
            EncodingPreservingFileIO.WriteAllTextAtomic(path, doc.OuterXml, encoding);

        return modified;
    }

    private static IEnumerable<string> EnumerateConfigFiles(string workingDir)
    {
        try
        {
            return Directory.EnumerateFiles(workingDir, "*.config", SearchOption.AllDirectories);
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
