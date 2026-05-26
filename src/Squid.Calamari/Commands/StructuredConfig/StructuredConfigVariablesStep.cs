using Squid.Calamari.Commands.Substitution;
using Squid.Calamari.Pipeline;

namespace Squid.Calamari.Commands.StructuredConfig;

/// <summary>
/// Wire-contract constants for the StructuredConfigVariables feature.
/// Public so cross-project drift tests in Squid.UnitTests can pin them.
/// </summary>
public static class StructuredConfigVariableNames
{
    public const string Enabled = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Enabled";
    public const string Targets = "Squid.Action.IISWebSite.StructuredConfigurationVariables.Targets";
}

/// <summary>
/// G1.3 — applies <see cref="JsonPathReplacer"/> to operator-nominated JSON
/// files. Closes the IIS handler's third advertised cross-cutting feature
/// (after G1.1 SubstituteInFiles + G1.2 ConfigurationTransforms).
///
/// <para><b>Pipeline position</b>: must run AFTER SubstituteInFilesStep so
/// any <c>#{Token}</c> references inside the JSON file are resolved first.
/// Then JSON-path replacement operates on concrete leaf values.</para>
///
/// <para><b>Scope</b>: targets are operator-supplied newline-separated globs.
/// Typically <c>appsettings.json</c> + <c>appsettings.{Env}.json</c>, but
/// works on any JSON file matching the glob.</para>
///
/// <para><b>Glob reuse</b>: <see cref="GlobMatcher"/> is shared with
/// SubstituteInFilesStep — single source of truth for glob semantics,
/// path-traversal sandbox, and recursive matching.</para>
/// </summary>
internal sealed class StructuredConfigVariablesStep : ExecutionStep<RunScriptCommandContext>
{
    public override bool IsEnabled(RunScriptCommandContext context)
    {
        if (context.Variables is null) return false;
        var raw = context.Variables.Get(StructuredConfigVariableNames.Enabled);
        return string.Equals(raw, "True", StringComparison.OrdinalIgnoreCase);
    }

    public override Task ExecuteAsync(RunScriptCommandContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(context.WorkingDirectory))
            throw new InvalidOperationException(
                "Working directory has not been initialized — StructuredConfigVariablesStep must run after ResolveWorkingDirectoryStep.");
        if (context.Variables is null)
            throw new InvalidOperationException(
                "Variables have not been loaded — StructuredConfigVariablesStep must run after LoadVariablesFromFilesStep.");

        var targetsRaw = context.Variables.Get(StructuredConfigVariableNames.Targets);
        if (string.IsNullOrWhiteSpace(targetsRaw)) return Task.CompletedTask;

        var totalReplaced = 0;
        var filesProcessed = 0;
        var filesFailed = 0;

        foreach (var glob in SplitGlobs(targetsRaw))
        {
            ct.ThrowIfCancellationRequested();

            var matches = GlobMatcher.Expand(context.WorkingDirectory, glob).ToList();
            if (matches.Count == 0)
            {
                Console.Error.WriteLine(
                    $"::warning::StructuredConfigVariables: glob '{glob}' matched no files under '{context.WorkingDirectory}'. Continuing.");
                continue;
            }

            foreach (var file in matches)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var result = JsonPathReplacer.Replace(json, context.Variables);

                    if (!result.Succeeded)
                    {
                        Console.Error.WriteLine(
                            $"::warning::StructuredConfigVariables: skipping '{file}' — {result.FailureReason}");
                        filesFailed++;
                        continue;
                    }

                    if (result.ReplacedCount > 0)
                    {
                        File.WriteAllText(file, result.Output);
                        Console.WriteLine(
                            $"StructuredConfigVariables: '{file}' — {result.ReplacedCount} leaf value(s) replaced.");
                    }

                    filesProcessed++;
                    totalReplaced += result.ReplacedCount;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine(
                        $"::warning::StructuredConfigVariables: failed to process '{file}' — {ex.GetType().Name}: {ex.Message}");
                    filesFailed++;
                }
            }
        }

        Console.WriteLine(
            $"StructuredConfigVariables: processed {filesProcessed} file(s), {filesFailed} failure(s), {totalReplaced} total leaf replacement(s).");

        return Task.CompletedTask;
    }

    private static IEnumerable<string> SplitGlobs(string raw)
        => raw.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0);
}
